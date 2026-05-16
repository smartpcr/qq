using System.Globalization;
using System.Security.Cryptography;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Telegram.Pipeline;

/// <summary>
/// Concrete inbound processing chain implementing
/// <see cref="ITelegramUpdatePipeline"/> for Stage 2.2 of
/// <c>implementation-plan.md</c>. Stages execute in fixed order:
/// classify → reserve → parse → authorize → resolve operator → enforce
/// role → route by event type → mark processed.
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
///   <item><description><b>Caught post-reservation exception</b> — once
///   <see cref="IDeduplicationService.TryReserveAsync"/> succeeds, EVERY
///   subsequent stage (parse, authorize, disambiguation-store write,
///   inline-button construction, role enforcement, the route switch
///   itself, and the post-route <see cref="IDeduplicationService.MarkProcessedAsync"/>
///   call) executes inside a single <c>try</c> that releases the
///   reservation before re-throwing. The narrower "wrap only the route
///   switch" shape would leak the reservation whenever a stage between
///   <c>TryReserveAsync</c> and the route — e.g. a transient
///   <see cref="IUserAuthorizationService.AuthorizeAsync"/> network
///   failure, a duplicate-token write in
///   <see cref="IPendingDisambiguationStore.StoreAsync"/>, or
///   <see cref="InlineButton.MaxCallbackDataBytes"/> validation
///   throwing on an oversized workspace id — threw. The webhook
///   controller would then surface a 500, Telegram would redeliver the
///   same update, <c>TryReserveAsync</c> would return <c>false</c>, and
///   the event would be silently dropped as a duplicate without ever
///   reaching a handler. Widening the catch closes that gap so the
///   brief's "subsequent delivery of evt-1 is processed normally (not
///   short-circuited as duplicate)" invariant holds for any caught
///   throw — not just exceptions emitted from the routed handler. The
///   throw still propagates so the webhook controller can transition
///   the corresponding <c>InboundUpdate</c> row to <c>Failed</c>.
///   </description></item>
///   <item><description><b>Uncaught crash</b> (process exits or the
///   <c>catch</c> block itself fails) — neither
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
///   (<see cref="OperationCanceledException"/>) — the catch's exception
///   filter deliberately lets <c>OperationCanceledException</c>
///   propagate WITHOUT releasing. Cancellation means the caller asked
///   us to stop, not that the event is retryable. The reservation
///   remains held; the durable <c>InboundUpdate</c> sweep is again
///   the recovery primitive.</description></item>
///   <item><description><b>Handler returns
///   <see cref="CommandResult.Success"/>=<c>false</c></b> — the
///   pipeline calls <see cref="IDeduplicationService.MarkProcessedAsync"/>
///   (NOT <see cref="IDeduplicationService.ReleaseReservationAsync"/>).
///   Rationale: the handler ran to completion and reported a
///   definitive failure response that the operator already saw, so
///   the event is treated as TERMINAL — live re-deliveries hammering
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
///   prompt — these return normally with
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
/// <see cref="PipelineResult.Handled"/>=<c>false</c> — it never consumes
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
/// keyboard via <see cref="PipelineResult.ResponseButtons"/> — one
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
/// workspace — a server-side handle that closes the iter-2 evaluator's
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
        : this(dedup, authz, parser, router, callbackHandler, pendingQuestions, pendingDisambiguations, timeProvider, logger, processedEventSink: null)
    {
    }

    /// <summary>
    /// Stage 2.6 constructor that accepts the
    /// <see cref="ProcessedMessengerEventChannel"/> sink the
    /// Stage 2.6 <see cref="TelegramMessengerConnector.ReceiveAsync"/>
    /// drains. Marked <see cref="ActivatorUtilitiesConstructorAttribute"/>
    /// so the DI container picks this overload when both ctors are
    /// available — without the attribute the container's "pick the
    /// constructor with the most parameters all of which can be
    /// resolved" heuristic would still choose this overload because
    /// AddTelegram registers <see cref="ProcessedMessengerEventChannel"/>
    /// as a singleton, but the explicit annotation pins the contract
    /// against future DI-rule changes and removes any ambiguity for
    /// reflection-based test harnesses.
    /// </summary>
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
        ProcessedMessengerEventChannel? processedEventSink)
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
    }

    /// <inheritdoc />
    public async Task<PipelineResult> ProcessAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messengerEvent);

        // Stage 2.6 connector feed (try/finally so EVERY exit — normal
        // returns, denials, duplicate short-circuits, handler failures,
        // AND caught-then-rethrown exceptions — publishes the event to
        // the ProcessedMessengerEventChannel that
        // TelegramMessengerConnector.ReceiveAsync drains). The publish
        // is fire-and-forget TryWrite: a saturated channel logs a
        // warning and continues without blocking the inbound hot path
        // (the durable InboundUpdate row remains the recovery primitive
        // — see ProcessedMessengerEventChannel remarks). When no sink is
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
        // and both invoke the handler (per implementation-plan.md §132 and
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
        // taken — parse, authorize, disambiguation-store write, inline-button
        // construction, role enforcement, the route switch, and the post-
        // route MarkProcessedAsync. The Stage 2.2 brief Scenario 4 invariant
        // ("subsequent delivery of evt-1 is processed normally, not short-
        // circuited as duplicate") applies to ANY caught post-reservation
        // throw, not just exceptions emitted from the routed handler. A
        // narrower wrap-only-the-switch shape leaks the reservation when an
        // earlier stage throws (transient authorize call, duplicate-token
        // store write, oversized-workspace-id button validation, …); the
        // webhook controller would surface a 500, Telegram would redeliver,
        // TryReserveAsync would return false, and the event would be
        // silently dropped without ever invoking a handler.
        //
        // Normal returns inside this try (denials, the multi-workspace
        // prompt, the success path) do NOT trigger the catch and
        // deliberately leave the reservation held — that is how the
        // pipeline prevents the same denial / prompt response from being
        // re-sent on a live re-delivery.
        //
        // The catch filter deliberately excludes OperationCanceledException
        // (caller asked us to stop) — those propagate without release;
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
                    return Denial(messengerEvent, PipelineResponses.CommandNotRecognized);
                }
            }

            // Stage: authorize. commandName drives Tier 1 (start) vs Tier 2 (binding) lookup.
            LogStage(messengerEvent, "authorize");
            var authz = await _authz.AuthorizeAsync(
                messengerEvent.UserId,
                messengerEvent.ChatId,
                parsed?.CommandName,
                ct).ConfigureAwait(false);

            // Defense-in-depth: BOTH the IsAuthorized boolean AND a non-empty
            // Bindings list are required. A well-behaved IUserAuthorizationService
            // sets these consistently (IsAuthorized == Bindings.Count > 0 per
            // implementation-plan.md §339), but a buggy or compromised provider
            // could return IsAuthorized=false alongside a stale binding list —
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
                return Denial(messengerEvent, PipelineResponses.Unauthorized);
            }

            // Stage: resolve operator.
            //
            // Multi-workspace prompt is scoped (iter-2 evaluator items 1 & 2)
            // to EXACTLY `/agents` with no arguments. Stage 3.2 only requires
            // the `/agents` no-argument disambiguation behavior per the brief:
            //
            //   * `/agents` (no args) with multiple bindings  → prompt
            //   * `/agents WORKSPACE` (explicit arg)          → fall through;
            //                                                    AgentsCommandHandler
            //                                                    validates the
            //                                                    explicit workspace
            //                                                    against the
            //                                                    operator's bindings
            //   * `/ask`, `/status`, `/pause`, `/handoff`, …  → fall through;
            //     (any non-`/agents` command)                   pick the first
            //                                                    binding and route
            //
            // The previous command-agnostic gate broke both `/agents WORKSPACE`
            // (intercepted before AgentsCommandHandler ever ran) and routed
            // other commands like `/handoff` into the disambiguation prompt
            // instead of their handlers — Stage 3.2 explicitly does not
            // require that behavior.
            LogStage(messengerEvent, "resolve-operator");
            var isAgentsNoArgPrompt = parsed is { CommandName: TelegramCommands.Agents }
                && parsed.Arguments.Count == 0;
            if (authz.Bindings.Count > 1
                && messengerEvent.EventType == EventType.Command
                && isAgentsNoArgPrompt)
            {
                var workspaceIds = authz.Bindings.Select(b => b.WorkspaceId).ToArray();

                // Persist a server-side disambiguation handle BEFORE emitting
                // the prompt. The token is the only reference Stage 3.3
                // receives via the callback — every other field needed to
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
                    "Pipeline disambiguation prompt: multiple bindings. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage} WorkspaceCount={Count} DisambiguationToken={Token}",
                    messengerEvent.CorrelationId,
                    messengerEvent.EventId,
                    "resolve-prompt",
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
                    _logger.LogWarning(
                        "Pipeline rejected: insufficient permissions. CorrelationId={CorrelationId} EventId={EventId} Stage={Stage} Command={Command} RequiredRole={Role}",
                        messengerEvent.CorrelationId,
                        messengerEvent.EventId,
                        "role-denied",
                        parsed.CommandName,
                        requiredRole);
                    return Denial(messengerEvent, PipelineResponses.InsufficientPermissions);
                }
            }

            // Stage: route. An exception from the routed handler is caught
            // by the outer try and triggers ReleaseReservationAsync so the
            // next live re-delivery is processed normally (Stage 2.2 brief
            // Step 2 / Scenario 4); the throw still propagates so the
            // webhook controller can mark the InboundUpdate row Failed. An
            // uncaught crash (process exit) leaves the reservation held —
            // Stage 2.4's sweep recovers via the durable InboundUpdate row.
            LogStage(messengerEvent, "route");
            CommandResult result;
            switch (messengerEvent.EventType)
            {
                case EventType.Command:
                    result = await _router.RouteAsync(parsed!, @operator, ct).ConfigureAwait(false);
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
                // survives the Stage 4.3 distributed-cache TTL — relying on
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
            // reaches the caller — diagnosing the underlying bug
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
    /// Iter-2 evaluator item 4 — the channel is unbounded so
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
                    "ProcessedMessengerEventChannel rejected write — channel completed (host shutting down). CorrelationId={CorrelationId} EventId={EventId} EventType={EventType}",
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
                "ProcessedMessengerEventChannel publish failed — swallowing so the original ProcessAsync outcome is preserved. CorrelationId={CorrelationId} EventId={EventId}",
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
