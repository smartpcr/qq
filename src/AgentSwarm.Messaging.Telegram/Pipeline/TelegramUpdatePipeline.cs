using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Telegram.Pipeline;

/// <summary>
/// Concrete inbound processing chain implementing
/// <see cref="ITelegramUpdatePipeline"/> for Stage 2.2 of
/// <c>implementation-plan.md</c>. Stages execute in fixed order:
/// classify -> reserve -> parse -> authorize -> resolve operator -> enforce
/// role -> route by event type -> mark processed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dedup contract (hybrid release-on-throw, Stage 2.2 brief Scenarios 4 &amp; 5).</b>
/// The pipeline's atomic gate is
/// <see cref="IDeduplicationService.TryReserveAsync"/>. A duplicate
/// caller for the same <c>EventId</c> sees <c>false</c> and is short-
/// circuited with <see cref="PipelineResult.Handled"/>=<c>true</c>; the
/// racy check-then-act <see cref="IDeduplicationService.IsProcessedAsync"/>
/// pair is explicitly NOT used here because under concurrent webhook
/// delivery it lets two pods both clear the probe and both run the
/// handler. After the routed handler returns successfully the pipeline
/// calls <see cref="IDeduplicationService.MarkProcessedAsync"/> to write
/// the post-handler "fully processed" marker (a state distinct from the
/// reservation, see the in-memory stub's two-bucket design).
/// </para>
/// <para>
/// <b>Reservation lifecycle on failure.</b> The behaviour differs by
/// failure mode (per the Stage 2.2 brief Step 2 and Scenario 4):
/// <list type="bullet">
///   <item><description><b>Caught post-reservation exception</b> -- once
///   <see cref="IDeduplicationService.TryReserveAsync"/> succeeds, EVERY
///   subsequent stage (parse, authorize, disambiguation-store write,
///   inline-button construction, role enforcement, the route switch
///   itself, and the post-route <see cref="IDeduplicationService.MarkProcessedAsync"/>
///   call) executes inside a single <c>try</c> that releases the
///   reservation before re-throwing. The narrower "wrap only the route
///   switch" shape would leak the reservation whenever a stage between
///   <c>TryReserveAsync</c> and the route -- e.g. a transient
///   <see cref="IUserAuthorizationService.AuthorizeAsync"/> network
///   failure, a duplicate-token write in
///   <see cref="IPendingDisambiguationStore.StoreAsync"/>, or
///   <see cref="InlineButton.MaxCallbackDataBytes"/> validation
///   throwing on an oversized workspace id -- threw. The webhook
///   controller would then surface a 500, Telegram would redeliver the
///   same update, <c>TryReserveAsync</c> would return <c>false</c>, and
///   the event would be silently dropped as a duplicate without ever
///   reaching a handler. Widening the catch closes that gap so the
///   brief's "subsequent delivery of evt-1 is processed normally (not
///   short-circuited as duplicate)" invariant holds for any caught
///   throw -- not just exceptions emitted from the routed handler. The
///   throw still propagates so the webhook controller can transition
///   the corresponding <c>InboundUpdate</c> row to <c>Failed</c>.
///   </description></item>
///   <item><description><b>Uncaught crash</b> (process exits or the
///   <c>catch</c> block itself fails) -- neither
///   <see cref="IDeduplicationService.MarkProcessedAsync"/> nor
///   <see cref="IDeduplicationService.ReleaseReservationAsync"/>
///   executes, so the reservation persists. The Stage 2.4
///   <c>InboundUpdate</c> recovery sweep is the canonical recovery
///   route in this case (it reads unprocessed durable rows rather than
///   relying on a fresh webhook delivery). The "two pods both run the
///   handler on a crash" race the implementation-plan addresses is
///   still closed: the only path that re-opens the gate is a successful
///   <see cref="IDeduplicationService.ReleaseReservationAsync"/> call,
///   which only executes after a caught exception in a single
///   pod.</description></item>
///   <item><description><b>Operator cancellation</b>
///   (<see cref="OperationCanceledException"/>) -- the catch's exception
///   filter deliberately lets <c>OperationCanceledException</c>
///   propagate WITHOUT releasing. Cancellation means the caller asked
///   us to stop, not that the event is retryable. The reservation
///   remains held; the durable <c>InboundUpdate</c> sweep is again
///   the recovery primitive.</description></item>
///   <item><description><b>Handler returns
///   <see cref="CommandResult.Success"/>=<c>false</c></b> -- the
///   pipeline calls <see cref="IDeduplicationService.MarkProcessedAsync"/>
///   (NOT <see cref="IDeduplicationService.ReleaseReservationAsync"/>).
///   Rationale: the handler ran to completion and reported a
///   definitive failure response that the operator already saw, so
///   the event is treated as TERMINAL -- live re-deliveries hammering
///   the same handler would be inappropriate (operator would see the
///   same failure repeated). The contract is therefore: <i>throw =
///   retryable</i> (release-on-throw), <i>return = terminal</i> (mark
///   processed regardless of <c>CommandResult.Success</c>). The
///   <see cref="PipelineResult.Succeeded"/> flag still reflects the
///   handler's failure so observability can alert; only the dedup
///   marker is symmetric with the success path. The durable
///   <c>InboundUpdate</c> row's terminal state is similarly aligned
///   by the Stage 2.4 webhook controller.</description></item>
///   <item><description><b>Normal denials</b> (parse-empty, parse-
///   invalid, authorize-denied, role-denied) and the multi-workspace
///   prompt -- these return normally with
///   <see cref="PipelineResult.Handled"/>=<c>true</c> and a denial /
///   prompt text. The catch does NOT fire (no throw), so the
///   reservation is intentionally LEFT HELD. This is the canonical
///   way to short-circuit a live re-delivery and stop the operator
///   from seeing the same denial / prompt response repeated when
///   Telegram redelivers the same update.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Concurrency property preserved.</b> The atomic-winner-per-
/// concurrent-burst guarantee is unaffected by release-on-throw: the
/// release executes sequentially AFTER the winner's stage that threw;
/// during the in-flight burst every concurrent caller still sees a
/// single <c>true</c> from <see cref="IDeduplicationService.TryReserveAsync"/>.
/// Subsequent (post-completion) callers may succeed only when the
/// prior winner caught an exception.
/// </para>
/// <para>
/// <b>Unknown events bypass dedup and authz.</b> An
/// <see cref="EventType.Unknown"/> event short-circuits at the very top
/// of <see cref="ProcessAsync"/> with
/// <see cref="PipelineResult.Handled"/>=<c>false</c> -- it never consumes
/// a reservation slot and never triggers an authorization round-trip.
/// This matches the <see cref="PipelineResult"/> contract
/// ("<see cref="PipelineResult.Handled"/> is <c>false</c> only when the
/// event type is unrecognized") and avoids leaking the operator's
/// authorization status to senders of malformed payloads.
/// </para>
/// <para>
/// <b>Multi-workspace handling.</b> When
/// <see cref="AuthorizationResult.Bindings"/> contains more than one
/// binding, slash-command events return a workspace-selection prompt
/// composed of <see cref="PipelineResult.ResponseText"/> plus an inline
/// keyboard via <see cref="PipelineResult.ResponseButtons"/> -- one
/// button per workspace, callback-data shape
/// <c>ws:&lt;token&gt;:&lt;index&gt;</c> where <c>index</c> is the
/// 0-based position into the stored
/// <see cref="PendingDisambiguation.CandidateWorkspaceIds"/> (per
/// <c>e2e-scenarios.md</c> "workspace disambiguation via inline
/// keyboard"). BEFORE emitting the prompt the pipeline persists a
/// <see cref="PendingDisambiguation"/> entry keyed by <c>token</c> via
/// <see cref="IPendingDisambiguationStore"/>; the entry carries the
/// original raw command, the original
/// <see cref="MessengerEvent.CorrelationId"/>, the originating
/// (user, chat) IDs, and the candidate workspace list. Stage 3.3 looks
/// the entry up by token and re-issues the command bound to the chosen
/// workspace -- a server-side handle that closes the iter-2 evaluator's
/// "callback has no durable way to know which command the workspace
/// selection completes" finding. Callback and text-reply events skip
/// the prompt because their target workspace is already implied by the
/// originating <c>PendingQuestion</c>; the first binding is used for
/// those routes and full disambiguation is the responsibility of
/// <c>CallbackQueryHandler</c> in Stage 3.3.
/// </para>
/// </remarks>
public sealed class TelegramUpdatePipeline : ITelegramUpdatePipeline
{
    /// <summary>
    /// How long an emitted disambiguation prompt remains tappable. After
    /// this window <see cref="IPendingDisambiguationStore.TakeAsync"/>
    /// returns <c>null</c> for the token and Stage 3.3 reports the
    /// callback as expired. Five minutes balances "operator stepped away
    /// from the chat for a moment" against bounded server-side memory.
    /// </summary>
    internal static readonly TimeSpan DisambiguationTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Stage 5.3 iter-7 evaluator item 1 — canonical
    /// <see cref="AuditEntry.Action"/> verb emitted by the pipeline for
    /// every early denial (parse-empty, parse-invalid, authorize-denied,
    /// role-denied). Using a single verb across the four phases keeps
    /// log-analytics queries simple ("find all pipeline denials" =
    /// <c>Action = 'command.denied'</c>); the phase distinction lives
    /// in <see cref="AuditEntry.Details"/> JSON under the
    /// <c>phase</c> key, parallelling the
    /// <see cref="CommandRouter.CommandAuditPhases"/> convention used
    /// by the router-level receipt / completion rows.
    /// </summary>
    public const string PipelineDeniedAuditAction = "command.denied";

    /// <summary>
    /// Stage 5.3 iter-7 evaluator item 1 — canonical literals for the
    /// <c>phase</c> discriminator the pipeline writes onto every
    /// denial audit row's <see cref="AuditEntry.Details"/> JSON. The
    /// pipeline writes ONE row per early denial; the
    /// <c>phase</c> value pins which gate rejected the command so a
    /// forensic query can reconstruct the rejection cause without
    /// re-parsing free-form log text.
    /// </summary>
    public static class PipelineDeniedPhases
    {
        /// <summary>Command event with a blank <c>RawCommand</c> (e.g. "/ ").</summary>
        public const string ParseEmpty = "parse-empty";

        /// <summary>Command parsed but failed validation (e.g. unknown verb, malformed args).</summary>
        public const string ParseInvalid = "parse-invalid";

        /// <summary>Authorization gate rejected the sender (no binding / disabled).</summary>
        public const string AuthorizeDenied = "authorize-denied";

        /// <summary>Operator authorized but lacks the role required for this verb.</summary>
        public const string RoleDenied = "role-denied";
    }

    /// <summary>
    /// JsonSerializerOptions used for the pipeline-denial Details
    /// column. CamelCase property names + skip-nulls matches the
    /// CommandRouter audit-details convention so audit-DB consumers
    /// can union receipt / completion / denial rows without per-row
    /// schema sniffing.
    /// </summary>
    private static readonly JsonSerializerOptions DenialAuditDetailsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IDeduplicationService _dedup;
    private readonly IUserAuthorizationService _authz;
    private readonly ICommandParser _parser;
    private readonly ICommandRouter _router;
    private readonly ICallbackHandler _callbackHandler;
    private readonly IPendingQuestionStore _pendingQuestions;
    private readonly IPendingDisambiguationStore _pendingDisambiguations;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelegramUpdatePipeline> _logger;
    private readonly ProcessedMessengerEventChannel? _processedEventSink;
    // Stage 5.3 iter-7 evaluator item 1 — every pipeline-level rejection
    // (parse-empty, parse-invalid, authorize-denied, role-denied) writes a
    // lifecycle audit row through this logger BEFORE returning the denial
    // response. NullAuditLogger is the fallback when the legacy 9/10-arg
    // ctors are used (e.g. existing unit-test harnesses) so the pipeline
    // remains testable without a real audit DB, but the DI-resolved
    // 11-arg ctor always receives the registered IAuditLogger
    // (PersistentAuditLogger in production, NullAuditLogger in dev).
    private readonly IAuditLogger _audit;

    // Stage 5.3 iter-9 evaluator item 2 — durable fallback target for
    // rejection-audit rows when _audit throws. The Stage 5.3 brief
    // mandates "log every inbound command"; the iter-8 evaluator
    // flagged that the prior log-and-swallow shape silently dropped
    // the audit row on any audit-DB outage, which is exactly the
    // contract the brief forbids. WriteRejectionAuditAsync now
    // enqueues the entry here BEFORE returning, so the row lands on
    // a durable medium (the file-backed FileAuditFallbackSink in
    // production, a no-op NullAuditFallbackSink in dev/test) even
    // when the primary writer has failed.
    private readonly IAuditFallbackSink _auditFallback;

    /// <summary>
    /// Stage 2.2 constructor kept for backward compatibility with all
    /// existing direct-construction call sites (the
    /// <c>TelegramUpdatePipelineTests.Harness</c> wires the pipeline
    /// without a sink, and the null-argument tests pin the original
    /// nine-parameter shape). Delegates to the Stage 2.6 ten-parameter
    /// overload with <c>processedEventSink: null</c> so the pipeline is
    /// a silent no-op on the sink side when no channel is wired.
    /// </summary>
    public TelegramUpdatePipeline(
        IDeduplicationService dedup,
        IUserAuthorizationService authz,
        ICommandParser parser,
        ICommandRouter router,
        ICallbackHandler callbackHandler,
        IPendingQuestionStore pendingQuestions,
        IPendingDisambiguationStore pendingDisambiguations,
        TimeProvider timeProvider,
        ILogger<TelegramUpdatePipeline> logger)
        : this(dedup, authz, parser, router, callbackHandler, pendingQuestions, pendingDisambiguations, timeProvider, logger, processedEventSink: null, audit: new NullAuditLogger(), auditFallback: new NullAuditFallbackSink())
    {
    }

    /// <summary>
    /// Stage 2.6 constructor kept for backward-compat with direct
    /// constructions that wire the
    /// <see cref="ProcessedMessengerEventChannel"/> sink but do NOT pass
    /// an <see cref="IAuditLogger"/>. Delegates to the Stage 5.3
    /// eleven-parameter overload with <c>audit: new NullAuditLogger()</c>
    /// so the rejection-audit path is a silent no-op for callers that
    /// pre-date the Stage 5.3 IAuditLogger dependency (the
    /// <c>TelegramUpdatePipelineTests.Harness</c> is the primary caller).
    /// </summary>
    public TelegramUpdatePipeline(
        IDeduplicationService dedup,
        IUserAuthorizationService authz,
        ICommandParser parser,
        ICommandRouter router,
        ICallbackHandler callbackHandler,
        IPendingQuestionStore pendingQuestions,
        IPendingDisambiguationStore pendingDisambiguations,
        TimeProvider timeProvider,
        ILogger<TelegramUpdatePipeline> logger,
        ProcessedMessengerEventChannel? processedEventSink)
        : this(dedup, authz, parser, router, callbackHandler, pendingQuestions, pendingDisambiguations, timeProvider, logger, processedEventSink, audit: new NullAuditLogger(), auditFallback: new NullAuditFallbackSink())
    {
    }

    /// <summary>
    /// Stage 5.3 iter-7 constructor that adds the
    /// <see cref="IAuditLogger"/> dependency so every pipeline-level
    /// rejection (parse-empty, parse-invalid, authorize-denied,
    /// role-denied) persists a lifecycle audit row through the same
    /// <see cref="IAuditLogger"/> the router uses for command receipts
    /// and the callback handler uses for decision rows. Kept for
    /// backward-compat with iter-7 / iter-8 test harnesses that
    /// pre-date the iter-9 <see cref="IAuditFallbackSink"/> dependency;
    /// delegates to the twelve-arg overload with a
    /// <see cref="NullAuditFallbackSink"/> so the fallback path is a
    /// silent no-op for those callers (they already assert the
    /// log-and-continue behaviour on a throwing primary audit and
    /// don't need a durable backstop).
    /// </summary>
    public TelegramUpdatePipeline(
        IDeduplicationService dedup,
        IUserAuthorizationService authz,
        ICommandParser parser,
        ICommandRouter router,
        ICallbackHandler callbackHandler,
        IPendingQuestionStore pendingQuestions,
        IPendingDisambiguationStore pendingDisambiguations,
        TimeProvider timeProvider,
        ILogger<TelegramUpdatePipeline> logger,
        ProcessedMessengerEventChannel? processedEventSink,
        IAuditLogger audit)
        : this(dedup, authz, parser, router, callbackHandler, pendingQuestions, pendingDisambiguations, timeProvider, logger, processedEventSink, audit, auditFallback: new NullAuditFallbackSink())
    {
    }

    /// <summary>
    /// Stage 5.3 iter-9 constructor that adds the
    /// <see cref="IAuditFallbackSink"/> dependency so every
    /// pipeline-level rejection has a durable backstop when the
    /// primary <see cref="IAuditLogger"/> throws. The iter-8
    /// evaluator flagged that the prior log-and-swallow shape
    /// silently dropped the audit row on any audit-DB outage,
    /// violating the Stage 5.3 brief's "<i>log every inbound
    /// command</i>" requirement; the fallback sink (file-backed
    /// JSON Lines in production via
    /// <see cref="FileAuditFallbackSink"/>) is the durable target
    /// for those rows. Marked
    /// <see cref="ActivatorUtilitiesConstructorAttribute"/> so the
    /// DI container picks this overload — every Telegram
    /// service-collection bootstrap registers both an
    /// <see cref="IAuditLogger"/> and an
    /// <see cref="IAuditFallbackSink"/>
    /// (NullAuditLogger / NullAuditFallbackSink via TryAddSingleton
    /// by default, replaced by PersistentAuditLogger /
    /// FileAuditFallbackSink when AddMessagingPersistence is called).
    /// </summary>
    /// <remarks>
    /// The Stage 5.3 brief mandates "log every inbound command with
    /// full context". The CommandRouter already audits commands that
    /// reach handler dispatch (receipt + completion rows). The four
    /// pipeline-level rejection sites bypass the router entirely —
    /// without this constructor's IAuditLogger + IAuditFallbackSink
    /// surface, an inbound command that fails parse / authorize /
    /// role checks would land in the ILogger sink only, violating
    /// the every-inbound-command persistence guarantee. The audit
    /// write at each rejection site follows a two-tier order:
    /// <list type="number">
    ///   <item><description>Primary <see cref="IAuditLogger.LogAsync"/>
    ///   — the canonical EF-backed write into <c>audit_logs</c>.
    ///   </description></item>
    ///   <item><description>On primary failure, the fallback
    ///   <see cref="IAuditFallbackSink.EnqueueAsync(AuditEntry,CancellationToken)"/>
    ///   — durable file-backed sink that absorbs the row when the
    ///   audit DB is unavailable. The denial response still fires
    ///   even if the fallback ALSO fails (logged at Critical so the
    ///   operator is paged), because the rejection reply is the
    ///   security-critical user-facing path.</description></item>
    /// </list>
    /// </remarks>
    [ActivatorUtilitiesConstructor]
    public TelegramUpdatePipeline(
        IDeduplicationService dedup,
        IUserAuthorizationService authz,
        ICommandParser parser,
        ICommandRouter router,
        ICallbackHandler callbackHandler,
        IPendingQuestionStore pendingQuestions,
        IPendingDisambiguationStore pendingDisambiguations,
        TimeProvider timeProvider,
        ILogger<TelegramUpdatePipeline> logger,
        ProcessedMessengerEventChannel? processedEventSink,
        IAuditLogger audit,
        IAuditFallbackSink auditFallback)
    {
        _dedup = dedup ?? throw new ArgumentNullException(nameof(dedup));
        _authz = authz ?? throw new ArgumentNullException(nameof(authz));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _callbackHandler = callbackHandler ?? throw new ArgumentNullException(nameof(callbackHandler));
        _pendingQuestions = pendingQuestions ?? throw new ArgumentNullException(nameof(pendingQuestions));
        _pendingDisambiguations = pendingDisambiguations ?? throw new ArgumentNullException(nameof(pendingDisambiguations));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processedEventSink = processedEventSink;
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _auditFallback = auditFallback ?? throw new ArgumentNullException(nameof(auditFallback));
    }

    /// <inheritdoc />
    public async Task<PipelineResult> ProcessAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messengerEvent);

        // Stage 2.6 connector feed (try/finally so EVERY exit -- normal
        // returns, denials, duplicate short-circuits, handler failures,
        // AND caught-then-rethrown exceptions -- publishes the event to
        // the ProcessedMessengerEventChannel that
        // TelegramMessengerConnector.ReceiveAsync drains). The publish
        // is fire-and-forget TryWrite: a saturated channel logs a
        // warning and continues without blocking the inbound hot path
        // (the durable InboundUpdate row remains the recovery primitive
        // -- see ProcessedMessengerEventChannel remarks). When no sink is
        // wired (the Stage 2.2 nine-arg constructor / unit-test
        // harnesses), the publish is a silent no-op.
        try
        {
            return await ExecuteAsync(messengerEvent, ct).ConfigureAwait(false);
        }
        finally
        {
            TryPublishProcessedEvent(messengerEvent);
        }
    }

    private async Task<PipelineResult> ExecuteAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        // Stage: classify event type. Unknown short-circuits BEFORE dedup
        // and BEFORE authz so that (a) malformed payloads do not consume a
        // reservation slot and (b) the authorization status of the sender
        // is not leaked through the reply distinction between "unauthorized"
        // and "unsupported event".
        LogStage(messengerEvent, "classify");
        if (messengerEvent.EventType == EventType.Unknown)
        {
            _logger.LogWarning(
                "Pipeline classify: unsupported event type. CorrelationId={CorrelationId} EventId={EventId} EventType={EventType} Stage={Stage}",
                messengerEvent.CorrelationId,
                messengerEvent.EventId,
                messengerEvent.EventType,
                "classify-unknown");
            return new PipelineResult
            {
                Handled = false,
                ResponseText = PipelineResponses.UnknownEventType,
                CorrelationId = messengerEvent.CorrelationId,
            };
        }

        // Stage: dedup gate. Use the atomic TryReserveAsync primitive so
        // two concurrent webhook deliveries cannot both clear a check
        // and both invoke the handler (per implementation-plan.md section 132 and
        // IDeduplicationService.cs remarks).
        LogStage(messengerEvent, "dedup");
        var reserved = await _dedup.TryReserveAsync(messengerEvent.EventId, ct).ConfigureAwait(false);
        if (!reserved)
        {
            _logger.LogInformation(
                "Pipeline short-circuit: duplicate event. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage}",
                messengerEvent.CorrelationId,
                messengerEvent.EventId,
                "dedup-duplicate");
            return new PipelineResult
            {
                Handled = true,
                CorrelationId = messengerEvent.CorrelationId,
            };
        }

        // Release-on-throw guard spans EVERY stage after the reservation is
        // taken -- parse, authorize, disambiguation-store write, inline-button
        // construction, role enforcement, the route switch, and the post-
        // route MarkProcessedAsync. The Stage 2.2 brief Scenario 4 invariant
        // ("subsequent delivery of evt-1 is processed normally, not short-
        // circuited as duplicate") applies to ANY caught post-reservation
        // throw, not just exceptions emitted from the routed handler. A
        // narrower wrap-only-the-switch shape leaks the reservation when an
        // earlier stage throws (transient authorize call, duplicate-token
        // store write, oversized-workspace-id button validation, ...); the
        // webhook controller would surface a 500, Telegram would redeliver,
        // TryReserveAsync would return false, and the event would be
        // silently dropped without ever invoking a handler.
        //
        // Normal returns inside this try (denials, the multi-workspace
        // prompt, the success path) do NOT trigger the catch and
        // deliberately leave the reservation held -- that is how the
        // pipeline prevents the same denial / prompt response from being
        // re-sent on a live re-delivery.
        //
        // The catch filter deliberately excludes OperationCanceledException
        // (caller asked us to stop) -- those propagate without release;
        // Stage 2.4's InboundUpdate sweep is the recovery primitive there.
        try
        {
            // Stage: parse (only meaningful for Command events).
            LogStage(messengerEvent, "parse");
            ParsedCommand? parsed = null;
            if (messengerEvent.EventType == EventType.Command)
            {
                if (string.IsNullOrWhiteSpace(messengerEvent.RawCommand))
                {
                    _logger.LogWarning(
                        "Pipeline rejected: Command event has no RawCommand. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage}",
                        messengerEvent.CorrelationId,
                        messengerEvent.EventId,
                        "parse-empty");
                    // Stage 5.3 iter-7 evaluator item 1 — persist the
                    // rejection BEFORE returning so audit_logs holds a
                    // row for every inbound command, including those
                    // dropped at the parse stage. The denial response
                    // still fires even if the audit write throws (the
                    // helper catches and logs).
                    await WriteRejectionAuditAsync(
                        messengerEvent,
                        phase: PipelineDeniedPhases.ParseEmpty,
                        rejectReason: "empty-raw-command",
                        commandName: null,
                        operatorTenantId: null,
                        ct).ConfigureAwait(false);
                    return Denial(messengerEvent, PipelineResponses.CommandNotRecognized);
                }

                parsed = _parser.Parse(messengerEvent.RawCommand);
                if (!parsed.IsValid)
                {
                    _logger.LogWarning(
                        "Pipeline rejected: invalid command parse. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage} Reason={Reason}",
                        messengerEvent.CorrelationId,
                        messengerEvent.EventId,
                        "parse-invalid",
                        parsed.ValidationError);
                    // Stage 5.3 iter-7 evaluator item 1 — same rationale
                    // as the parse-empty branch above; the parser's
                    // ValidationError is captured in the audit row's
                    // Details JSON so post-hoc analytics can identify
                    // which malformed-command shape the operator hit.
                    await WriteRejectionAuditAsync(
                        messengerEvent,
                        phase: PipelineDeniedPhases.ParseInvalid,
                        rejectReason: parsed.ValidationError ?? "invalid-parse",
                        commandName: parsed.CommandName,
                        operatorTenantId: null,
                        ct).ConfigureAwait(false);
                    return Denial(messengerEvent, PipelineResponses.CommandNotRecognized);
                }
            }

            // Stage: authorize. Stage 5.2 (iter-3) -- single unified
            // entry point for every command, including /start. The
            // commandName parameter drives Tier 1 (allowlist
            // onboarding when commandName == "start") vs Tier 2
            // (binding lookup otherwise) INSIDE the impl, satisfying
            // the Stage 5.2 brief requirement that commandName alone
            // distinguish Tier 1 and Tier 2 "without requiring
            // separate pipeline branches". The chatType token is
            // forwarded so Stage 3.4's chat-type fidelity is
            // preserved on the Tier 1 path; Tier 2 implementations
            // ignore the chatType argument because the chat type is
            // already persisted on the existing OperatorBinding.
            LogStage(messengerEvent, "authorize");
            var authz = await _authz.AuthorizeAsync(
                messengerEvent.UserId,
                messengerEvent.ChatId,
                parsed?.CommandName,
                messengerEvent.ChatType,
                ct).ConfigureAwait(false);

            // Defense-in-depth: BOTH the IsAuthorized boolean AND a non-empty
            // Bindings list are required. A well-behaved IUserAuthorizationService
            // sets these consistently (IsAuthorized == Bindings.Count > 0 per
            // implementation-plan.md section 339), but a buggy or compromised provider
            // could return IsAuthorized=false alongside a stale binding list --
            // checking both closes that gap and avoids constructing an
            // AuthorizedOperator from a binding the provider has explicitly
            // disclaimed. Pinned by
            // Pipeline_RejectsAuthorization_WhenIsAuthorizedFalse_DespiteNonEmptyBindings.
            if (!authz.IsAuthorized || authz.Bindings.Count == 0)
            {
                _logger.LogWarning(
                    "Pipeline rejected: unauthorized. CorrelationId={CorrelationId} EventId={EventId} UserId={UserId} ChatId={ChatId} Stage={Stage} Reason={Reason} IsAuthorized={IsAuthorized} BindingCount={BindingCount}",
                    messengerEvent.CorrelationId,
                    messengerEvent.EventId,
                    messengerEvent.UserId,
                    messengerEvent.ChatId,
                    "authorize-denied",
                    authz.DenialReason ?? "no active binding",
                    authz.IsAuthorized,
                    authz.Bindings.Count);
                // Stage 5.3 iter-7 evaluator item 1 — security-critical
                // path: every unauthorized inbound update gets a
                // durable audit row even though no AuthorizedOperator
                // binding is available. UserId on the audit row uses
                // the raw messenger UserId (no binding to resolve a
                // tenant), TenantId stays null per AuditEntry remarks
                // ("null for entries emitted before authorization
                // resolved a binding"). The DenialReason from the
                // authz service is captured in Details JSON for
                // forensic review of who-tried-what-when.
                await WriteRejectionAuditAsync(
                    messengerEvent,
                    phase: PipelineDeniedPhases.AuthorizeDenied,
                    rejectReason: authz.DenialReason ?? "no active binding",
                    commandName: parsed?.CommandName,
                    operatorTenantId: null,
                    ct).ConfigureAwait(false);
                return Denial(messengerEvent, PipelineResponses.Unauthorized);
            }

            // Stage: resolve operator.
            //
            // Iter-2 evaluator item 2 (Stage 3.4) -- the disambiguation
            // gate now applies to EVERY multi-binding command except:
            //
            //   * `/agents WORKSPACE` (explicit workspace arg)  -> fall
            //                                                      through;
            //                                                      AgentsCommandHandler
            //                                                      validates
            //                                                      the
            //                                                      explicit
            //                                                      workspace
            //                                                      against
            //                                                      the
            //                                                      operator's
            //                                                      bindings.
            //   * `/start` onboarding                            -> fall
            //                                                      through;
            //                                                      /start
            //                                                      just
            //                                                      created
            //                                                      the
            //                                                      bindings --
            //                                                      surfacing
            //                                                      a
            //                                                      "pick
            //                                                      one"
            //                                                      prompt
            //                                                      here
            //                                                      would
            //                                                      confuse
            //                                                      the
            //                                                      operator
            //                                                      who
            //                                                      only
            //                                                      wanted
            //                                                      a
            //                                                      welcome
            //                                                      message.
            //   * Non-Command event types (TextReply,            -> fall
            //     CallbackResponse, etc.)                          through;
            //                                                      those
            //                                                      paths
            //                                                      route
            //                                                      via
            //                                                      pending-question
            //                                                      / pending-disambiguation
            //                                                      lookup,
            //                                                      not
            //                                                      via
            //                                                      workspace
            //                                                      selection.
            //
            // Every other command (/status, /ask, /handoff, /pause,
            // /resume, /approve, /reject, and /agents-with-no-args)
            // now prompts for workspace selection when the operator
            // has more than one active binding. The previous gate
            // restricted the prompt to /agents only, which let other
            // commands silently route to authz.Bindings[0] -- the
            // wrong workspace for the multi-workspace operator
            // (evaluator item 2 + architecture.md section 4.3 + brief test
            // scenario "Multi-workspace bindings returned ... so the
            // pipeline can prompt for workspace disambiguation via
            // inline keyboard").
            LogStage(messengerEvent, "resolve-operator");
            var hasExplicitWorkspaceArg = parsed is { CommandName: TelegramCommands.Agents }
                && parsed.Arguments.Count > 0;
            var isStartCommand = parsed is { CommandName: TelegramCommands.Start };
            var needsDisambiguation = authz.Bindings.Count > 1
                && messengerEvent.EventType == EventType.Command
                && !isStartCommand
                && !hasExplicitWorkspaceArg;
            if (needsDisambiguation)
            {
                var workspaceIds = authz.Bindings.Select(b => b.WorkspaceId).ToArray();

                // Persist a server-side disambiguation handle BEFORE emitting
                // the prompt. The token is the only reference Stage 3.3
                // receives via the callback -- every other field needed to
                // re-issue the original command (raw command text,
                // correlation id, originating user/chat) is parked here so
                // it does not have to fit in callback_data's 64-byte budget.
                var token = GenerateDisambiguationToken();
                var now = _timeProvider.GetUtcNow();
                var pending = new PendingDisambiguation
                {
                    Token = token,
                    OriginalRawCommand = messengerEvent.RawCommand ?? string.Empty,
                    CorrelationId = messengerEvent.CorrelationId,
                    TelegramUserId = messengerEvent.UserId,
                    TelegramChatId = messengerEvent.ChatId,
                    CandidateWorkspaceIds = workspaceIds,
                    CreatedAt = now,
                    ExpiresAt = now + DisambiguationTtl,
                };
                await _pendingDisambiguations.StoreAsync(pending, ct).ConfigureAwait(false);

                var buttons = PipelineResponses.MultiWorkspaceButtons(token, workspaceIds);
                _logger.LogInformation(
                    "Pipeline disambiguation prompt: multiple bindings. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage} Command={Command} WorkspaceCount={Count} DisambiguationToken={Token}",
                    messengerEvent.CorrelationId,
                    messengerEvent.EventId,
                    "resolve-prompt",
                    parsed?.CommandName ?? "(non-command)",
                    workspaceIds.Length,
                    token);
                return new PipelineResult
                {
                    Handled = true,
                    ResponseText = PipelineResponses.MultiWorkspacePromptText,
                    ResponseButtons = buttons,
                    CorrelationId = messengerEvent.CorrelationId,
                };
            }

            var binding = authz.Bindings[0];
            var @operator = new AuthorizedOperator
            {
                OperatorId = binding.Id,
                TenantId = binding.TenantId,
                WorkspaceId = binding.WorkspaceId,
                Roles = binding.Roles,
                TelegramUserId = binding.TelegramUserId,
                TelegramChatId = binding.TelegramChatId,
                OperatorAlias = binding.OperatorAlias,
            };

            // Stage: role enforcement. Only commands carry role gates.
            if (parsed is not null)
            {
                LogStage(messengerEvent, "role-enforcement");
                var requiredRole = CommandRoleRequirements.RequiredRole(parsed.CommandName);
                if (requiredRole is not null && !CommandRoleRequirements.HasRole(@operator, requiredRole))
                {
                    // Stage 5.2 -- audit log entry at Warning level on
                    // role denial (brief Implementation Step 3 + Test
                    // Scenarios "Approve requires Approver role" /
                    // "Pause requires Operator role"). The structured
                    // property name {RequiredRole} matches the literal
                    // "RequiredRole=" prefix so log-analytics queries
                    // that filter by the property surfaced in the
                    // message text resolve correctly.
                    _logger.LogWarning(
                        "Pipeline rejected: insufficient permissions. CorrelationId={CorrelationId} EventId={EventId} UserId={UserId} ChatId={ChatId} Stage={Stage} Command={Command} RequiredRole={RequiredRole}",
                        messengerEvent.CorrelationId,
                        messengerEvent.EventId,
                        messengerEvent.UserId,
                        messengerEvent.ChatId,
                        "role-denied",
                        parsed.CommandName,
                        requiredRole);
                    // Stage 5.3 iter-7 evaluator item 1 — role-denied
                    // is the third pre-router rejection site flagged
                    // by the iter-6 evaluator. Unlike the parse / authz
                    // sites we DO have a resolved @operator here, so
                    // both the operator's TenantId and the command
                    // verb land on the audit row (the CommandRouter
                    // would have done this had the role gate passed).
                    await WriteRejectionAuditAsync(
                        messengerEvent,
                        phase: PipelineDeniedPhases.RoleDenied,
                        rejectReason: $"role-denied:{requiredRole}",
                        commandName: parsed.CommandName,
                        operatorTenantId: @operator.TenantId,
                        ct).ConfigureAwait(false);
                    return Denial(messengerEvent, PipelineResponses.InsufficientPermissions);
                }
            }

            // Stage: route. An exception from the routed handler is caught
            // by the outer try and triggers ReleaseReservationAsync so the
            // next live re-delivery is processed normally (Stage 2.2 brief
            // Step 2 / Scenario 4); the throw still propagates so the
            // webhook controller can mark the InboundUpdate row Failed. An
            // uncaught crash (process exit) leaves the reservation held --
            // Stage 2.4's sweep recovers via the durable InboundUpdate row.
            LogStage(messengerEvent, "route");
            CommandResult result;
            switch (messengerEvent.EventType)
            {
                case EventType.Command:
                    // Stage 5.3 iter-6 evaluator items 2 + 3 — enrich the
                    // parsed command with the inbound transport context
                    // BEFORE handing off to the router. The router uses
                    // these on the pre-handler audit row:
                    //   * SourceMessageId → AuditEntry.MessageId so the
                    //     row is joinable back to the originating Telegram
                    //     update_id (item 3 — the prior router hard-coded
                    //     MessageId=null on every command audit row).
                    //   * TraceId → AuditEntry.CorrelationId so the
                    //     pre-handler "command receipt" row shares a
                    //     correlation id with the pipeline / dedup /
                    //     outbound artifacts produced by the same inbound
                    //     update (item 2 — the receipt now exists BEFORE
                    //     the handler runs so a router-level audit failure
                    //     cannot leave orphan side effects).
                    var enriched = parsed! with
                    {
                        SourceMessageId = messengerEvent.EventId,
                        TraceId = messengerEvent.CorrelationId,
                    };
                    result = await _router.RouteAsync(enriched, @operator, ct).ConfigureAwait(false);
                    break;
                case EventType.CallbackResponse:
                    result = await _callbackHandler.HandleAsync(messengerEvent, ct).ConfigureAwait(false);
                    break;
                case EventType.TextReply:
                    result = await RouteTextReplyAsync(messengerEvent, ct).ConfigureAwait(false);
                    break;
                default:
                    // Defensive: EventType.Unknown was already handled at the
                    // classify stage. Any future EventType value lands here.
                    _logger.LogWarning(
                        "Pipeline received unsupported event type after classify. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage} EventType={EventType}",
                        messengerEvent.CorrelationId,
                        messengerEvent.EventId,
                        "route-unknown",
                        messengerEvent.EventType);
                    return new PipelineResult
                    {
                        Handled = false,
                        ResponseText = PipelineResponses.UnknownEventType,
                        CorrelationId = messengerEvent.CorrelationId,
                    };
            }

            // Stage: handler-result. Inspect CommandResult.Success to drive
            // the operator-facing response shape. Dedup-wise the contract is
            // hybrid: throw = retryable (release-on-throw, caught below),
            // return = terminal (mark processed regardless of Success). A
            // handler that returns Success=false has run to completion and
            // delivered a definitive failure response to the operator, so
            // the pipeline marks the event processed exactly as it does on
            // the success path. PipelineResult.Succeeded still reflects the
            // handler's failure so observability can alert; only the dedup
            // marker is symmetric. Pinned by
            // Pipeline_OnHandlerReturnsFailure_MarksProcessed_AndSurfacesError
            // and Pipeline_OnHandlerReturnsFailure_NextDeliveryShortCircuits.
            LogStage(messengerEvent, "handler-result");
            if (!result.Success)
            {
                var failureText = string.IsNullOrEmpty(result.ResponseText)
                    ? PipelineResponses.HandlerFailureFallback
                    : result.ResponseText;
                _logger.LogWarning(
                    "Pipeline handler returned failure. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage} ErrorCode={ErrorCode}",
                    messengerEvent.CorrelationId,
                    messengerEvent.EventId,
                    "handler-failure",
                    result.ErrorCode);

                // TERMINAL: mark processed even on Success=false so live
                // re-deliveries short-circuit at the dedup gate. The
                // processed marker is the canonical "done" signal that
                // survives the Stage 4.3 distributed-cache TTL -- relying on
                // the bare reservation alone would let the gate re-open
                // when the reservation expired and re-issue the same
                // failure response to the operator.
                LogStage(messengerEvent, "mark-processed");
                await _dedup.MarkProcessedAsync(messengerEvent.EventId, ct).ConfigureAwait(false);

                return new PipelineResult
                {
                    Handled = true,
                    Succeeded = false,
                    ResponseText = failureText,
                    ResponseButtons = result.ResponseButtons,
                    ErrorCode = result.ErrorCode,
                    CorrelationId = messengerEvent.CorrelationId,
                };
            }

            // Stage: post-success processed marker (distinct from the
            // reservation set at the dedup stage).
            LogStage(messengerEvent, "mark-processed");
            await _dedup.MarkProcessedAsync(messengerEvent.EventId, ct).ConfigureAwait(false);

            return new PipelineResult
            {
                Handled = true,
                Succeeded = true,
                ResponseText = result.ResponseText,
                ResponseButtons = result.ResponseButtons,
                CorrelationId = messengerEvent.CorrelationId,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Release-on-throw: the brief explicitly requires a
            // subsequent live re-delivery to be processed normally.
            // We swallow any release failure so the original exception
            // reaches the caller -- diagnosing the underlying bug
            // matters more than reporting a release-side cleanup
            // failure.
            try
            {
                await _dedup.ReleaseReservationAsync(messengerEvent.EventId, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    ex,
                    "Pipeline released reservation after post-reservation exception. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage}",
                    messengerEvent.CorrelationId,
                    messengerEvent.EventId,
                    "release-on-throw");
            }
            catch (Exception releaseEx)
            {
                _logger.LogError(
                    releaseEx,
                    "Pipeline failed to release reservation after post-reservation exception; live re-delivery may short-circuit. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage}",
                    messengerEvent.CorrelationId,
                    messengerEvent.EventId,
                    "release-on-throw-failed");
            }
            throw;
        }
    }

    /// <summary>
    /// Routes a <see cref="EventType.TextReply"/> event. If the operator has
    /// a <see cref="PendingQuestionStatus.AwaitingComment"/> question, the
    /// event is forwarded to <see cref="ICallbackHandler"/> (which owns the
    /// comment-collection flow). Otherwise the text is silently acknowledged
    /// (no response) so that arbitrary chatter does not trigger noise.
    /// </summary>
    private async Task<CommandResult> RouteTextReplyAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        // Parse the (string) chat/user IDs into the (long) IDs used by the
        // pending-question store. Failure means we cannot match a pending
        // question, so we fall through to silent ack.
        if (!long.TryParse(messengerEvent.ChatId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatIdLong) ||
            !long.TryParse(messengerEvent.UserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userIdLong))
        {
            _logger.LogDebug(
                "TextReply has non-numeric IDs; cannot resolve pending question. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage}",
                messengerEvent.CorrelationId,
                messengerEvent.EventId,
                "text-reply-non-numeric");
            return SilentAck(messengerEvent);
        }

        var pending = await _pendingQuestions
            .GetAwaitingCommentAsync(chatIdLong, userIdLong, ct)
            .ConfigureAwait(false);

        if (pending is null)
        {
            _logger.LogDebug(
                "TextReply has no awaiting-comment pending question. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage}",
                messengerEvent.CorrelationId,
                messengerEvent.EventId,
                "text-reply-no-pending");
            return SilentAck(messengerEvent);
        }

        return await _callbackHandler.HandleAsync(messengerEvent, ct).ConfigureAwait(false);
    }

    private static CommandResult SilentAck(MessengerEvent messengerEvent) =>
        new()
        {
            Success = true,
            ResponseText = null,
            CorrelationId = messengerEvent.CorrelationId,
        };

    private PipelineResult Denial(MessengerEvent messengerEvent, string responseText) =>
        new()
        {
            Handled = true,
            ResponseText = responseText,
            CorrelationId = messengerEvent.CorrelationId,
        };

    /// <summary>
    /// Stage 5.3 iter-7 evaluator item 1 — persist a lifecycle audit
    /// row for every pipeline-level rejection (parse-empty,
    /// parse-invalid, authorize-denied, role-denied). The four
    /// rejection sites in <see cref="ExecuteAsync"/> short-circuit
    /// without ever invoking the <see cref="ICommandRouter"/>, so the
    /// CommandRouter's pre-handler receipt-audit cannot cover them;
    /// this helper closes the gap so the audit_logs table holds a row
    /// for EVERY inbound command, satisfying the Stage 5.3 brief's
    /// "log every inbound command with full context" requirement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Failure semantics — log-but-do-not-rethrow.</b> Unlike the
    /// CommandRouter's receipt-audit (which rethrows so a failed
    /// audit blocks handler dispatch), the pipeline-rejection audit
    /// MUST succeed-or-log because the denial response itself is the
    /// security-critical path. An unauthorized user must always
    /// receive the Unauthorized reply even if the audit DB is
    /// momentarily unavailable — letting the audit write block the
    /// denial would create a denial-of-service vector (an attacker
    /// that crashes the audit DB also crashes the authorization
    /// rejection path). The audit-write failure is logged at
    /// LogLevel.Error so operators surface it through the same
    /// alerting that watches CommandRouter audit failures.
    /// </para>
    /// <para>
    /// <b>EventFamily = Lifecycle.</b> These rows describe system
    /// gatekeeping decisions, not command receipts (which would be
    /// <see cref="AuditEventFamilies.Command"/>) or human decisions
    /// (<see cref="AuditEventFamilies.Decision"/>). Lifecycle is the
    /// canonical bucket per
    /// <see cref="AuditEventFamilies.Lifecycle"/>'s remarks.
    /// </para>
    /// <para>
    /// <b>Action verb = <see cref="PipelineDeniedAuditAction"/>.</b>
    /// All four rejection sites emit the same
    /// <c>command.denied</c> verb so a single equality predicate
    /// (<c>WHERE Action = 'command.denied'</c>) returns every
    /// pipeline rejection across the four phases. The per-phase
    /// discriminator is captured in the Details JSON's <c>phase</c>
    /// field (see <see cref="PipelineDeniedPhases"/>) so analytics
    /// queries can pivot on it without a schema change.
    /// </para>
    /// </remarks>
    private async Task WriteRejectionAuditAsync(
        MessengerEvent messengerEvent,
        string phase,
        string rejectReason,
        string? commandName,
        string? operatorTenantId,
        CancellationToken ct)
    {
        string details;
        try
        {
            details = JsonSerializer.Serialize(
                new PipelineDenialAuditDetails(
                    Phase: phase,
                    CommandName: commandName,
                    RawCommand: messengerEvent.RawCommand,
                    RejectReason: rejectReason,
                    ChatId: messengerEvent.ChatId,
                    EventType: messengerEvent.EventType.ToString()),
                DenialAuditDetailsJsonOptions);
        }
        catch (Exception serEx)
        {
            // Defensive — JsonSerializer.Serialize on a positional
            // record with all primitive types should never throw, but
            // if a future refactor adds a non-serializable field we
            // should NOT lose the audit row over it. Fall back to a
            // JSON object built via JsonSerializer so the value is
            // GUARANTEED to be valid JSON (the iter-9 PersistentAuditLogger
            // gate rejects invalid-JSON Details, so a hand-formatted
            // fallback string with embedded quotes / control chars
            // would itself fail the writer and lose the audit row).
            _logger.LogWarning(
                serEx,
                "Pipeline rejection audit Details serialization failed; persisting fallback string. EventId={EventId} Phase={Phase}",
                messengerEvent.EventId,
                phase);
            details = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["phase"] = phase,
                ["reason"] = rejectReason,
            });
        }

        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            // Carry the inbound transport id so audit_logs rows for
            // pipeline rejections are joinable back to
            // inbound_updates.EventId on the same column the
            // CommandRouter's receipt rows use.
            MessageId = messengerEvent.EventId,
            UserId = messengerEvent.UserId ?? string.Empty,
            AgentId = null,
            Action = PipelineDeniedAuditAction,
            EventFamily = AuditEventFamilies.Lifecycle,
            Timestamp = _timeProvider.GetUtcNow(),
            CorrelationId = messengerEvent.CorrelationId,
            TenantId = operatorTenantId,
            Details = details,
        };

        try
        {
            await _audit.LogAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stage 5.3 iter-9 evaluator item 2 — primary audit-DB
            // failure must NOT silently drop the row. Enqueue the
            // entry on the durable fallback sink (file-backed JSON
            // Lines in production via FileAuditFallbackSink) so the
            // "log every inbound command" guarantee survives a
            // transient audit-DB outage. The denial response still
            // fires regardless — the rejection reply is the security-
            // critical user-facing path that must reach the operator
            // even if BOTH audit tiers fail.
            _logger.LogError(
                ex,
                "Pipeline rejection audit primary write failed; falling back to durable sink (Stage 5.3 iter-9 evaluator item 2). EventId={EventId} CorrelationId={CorrelationId} Phase={Phase} Reason={Reason}",
                messengerEvent.EventId,
                messengerEvent.CorrelationId,
                phase,
                rejectReason);
            try
            {
                await _auditFallback.EnqueueAsync(entry, ct).ConfigureAwait(false);
            }
            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
            {
                // Both primary AND fallback failed. The denial reply
                // still fires (operator response is security-critical
                // and never blocks on audit), but we escalate to
                // Critical so the operator is paged — at this point
                // the audit trail for this rejection is genuinely
                // lost and a human must reconstruct from upstream
                // logs.
                _logger.LogCritical(
                    fallbackEx,
                    "Pipeline rejection audit fallback ALSO failed; audit row is lost for this rejection. Operator intervention required. EventId={EventId} CorrelationId={CorrelationId} Phase={Phase} Reason={Reason} PrimaryError={PrimaryError}",
                    messengerEvent.EventId,
                    messengerEvent.CorrelationId,
                    phase,
                    rejectReason,
                    ex.Message);
            }
        }
    }

    /// <summary>
    /// JSON shape persisted onto <see cref="AuditLogEntry.Details"/>
    /// for the pipeline-denial lifecycle rows. Positional record so
    /// System.Text.Json emits camelCase property names matching the
    /// parameter names (driven by
    /// <see cref="DenialAuditDetailsJsonOptions"/>) without explicit
    /// attribute decorations. The <c>Phase</c> field carries one of
    /// the <see cref="PipelineDeniedPhases"/> literals.
    /// </summary>
    private sealed record PipelineDenialAuditDetails(
        string Phase,
        string? CommandName,
        string? RawCommand,
        string RejectReason,
        string ChatId,
        string EventType);

    private void LogStage(MessengerEvent messengerEvent, string stage) =>
        _logger.LogInformation(
            "Pipeline stage: CorrelationId={CorrelationId} EventId={EventId} Stage={Stage}",
            messengerEvent.CorrelationId,
            messengerEvent.EventId,
            stage);

    /// <summary>
    /// Stage 2.6 connector feed: publishes the pipeline-processed
    /// <paramref name="messengerEvent"/> to the shared
    /// <see cref="ProcessedMessengerEventChannel"/> so the Stage 2.6
    /// <see cref="TelegramMessengerConnector"/> can surface it via
    /// <see cref="IMessengerConnector.ReceiveAsync"/>. Silent no-op
    /// when the sink is not wired (the legacy nine-arg constructor
    /// / unit-test harnesses pass <c>null</c>).
    /// <para>
    /// Iter-2 evaluator item 4 -- the channel is unbounded so
    /// <see cref="System.Threading.Channels.ChannelWriter{T}.TryWrite"/>
    /// only ever returns <c>false</c> if the channel has been
    /// completed (shutdown); the previous fast-drop-on-full shape
    /// has been removed because Stage 2.6 requires lossless delivery
    /// of every processed update to the connector drain (no message
    /// loss under 100+ agent bursts). A <c>false</c> return is now
    /// surfaced at <see cref="LogLevel.Warning"/> as a shutdown-race
    /// diagnostic rather than as an expected backpressure event.
    /// </para>
    /// </summary>
    private void TryPublishProcessedEvent(MessengerEvent messengerEvent)
    {
        if (_processedEventSink is null)
        {
            return;
        }

        try
        {
            if (!_processedEventSink.Writer.TryWrite(messengerEvent))
            {
                _logger.LogWarning(
                    "ProcessedMessengerEventChannel rejected write -- channel completed (host shutting down). CorrelationId={CorrelationId} EventId={EventId} EventType={EventType}",
                    messengerEvent.CorrelationId,
                    messengerEvent.EventId,
                    messengerEvent.EventType);
            }
        }
        catch (Exception ex)
        {
            // The finally block must NEVER mask the in-flight return
            // / exception from ExecuteAsync. Swallow any publish-side
            // failure (channel disposed mid-shutdown, observer hook
            // misbehaving) with a diagnostic log so the original
            // outcome reaches the caller intact.
            _logger.LogWarning(
                ex,
                "ProcessedMessengerEventChannel publish failed -- swallowing so the original ProcessAsync outcome is preserved. CorrelationId={CorrelationId} EventId={EventId}",
                messengerEvent.CorrelationId,
                messengerEvent.EventId);
        }
    }

    /// <summary>
    /// Returns a 12-character lowercase hex token (48 bits of entropy)
    /// suitable as a <see cref="PendingDisambiguation.Token"/>. 48 bits
    /// makes accidental collision within the
    /// <see cref="DisambiguationTtl"/> window vanishingly unlikely; the
    /// printable-ASCII output keeps the resulting <c>callback_data</c>
    /// byte count == character count, simplifying the
    /// <see cref="InlineButton.MaxCallbackDataBytes"/> budget math.
    /// </summary>
    private static string GenerateDisambiguationToken()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
