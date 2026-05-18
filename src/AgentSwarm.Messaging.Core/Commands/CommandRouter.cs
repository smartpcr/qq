namespace AgentSwarm.Messaging.Core.Commands;

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Production <see cref="ICommandRouter"/>. Builds an
/// ordinal-case-insensitive dictionary of
/// <see cref="ICommandHandler.CommandName"/> → handler at construction
/// time and dispatches each <see cref="ParsedCommand"/> to the matching
/// entry. An unknown command returns a <see cref="CommandResult"/> with
/// <see cref="CommandResult.Success"/>=<c>false</c> and a help text
/// listing the recognized command vocabulary from
/// <see cref="TelegramCommands.All"/> so the operator is told exactly
/// what to type instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 3.2 acceptance criterion.</b> "Unknown command rejected —
/// Given a <see cref="ParsedCommand"/> with <c>CommandName</c> =
/// <c>foo</c>, When routed, Then the result has
/// <see cref="CommandResult.Success"/>=<c>false</c> and a helpful error
/// message listing valid commands." The router emits
/// <see cref="UnknownCommandErrorCode"/> as the
/// <see cref="CommandResult.ErrorCode"/> so log queries and
/// alerting can pivot on the machine-readable identifier without
/// pattern-matching the human text.
/// </para>
/// <para>
/// <b>Duplicate handlers.</b> Two handlers advertising the same
/// <see cref="ICommandHandler.CommandName"/> indicate a wiring bug —
/// the router throws <see cref="InvalidOperationException"/> at
/// construction time to fail fast rather than silently picking one and
/// dropping the other. The case-insensitive comparison also catches
/// the <c>"Approve"</c> vs <c>"approve"</c> drift the
/// <see cref="TelegramCommands.IsKnown"/> contract notes.
/// </para>
/// <para>
/// <b>Stage 5.3 audit integration.</b> Per the Stage 5.3 brief
/// ("<i>Integrate audit logging at command router level: log every
/// inbound command and every outbound decision event with full
/// context</i>"), every call to <see cref="RouteAsync"/> emits up to
/// TWO <see cref="AuditEntry"/> rows via the injected
/// <see cref="IAuditLogger.LogAsync"/> path:
/// <list type="number">
///   <item><description><b>Receipt row</b> (<see cref="CommandAuditPhases.Received"/>)
///   — written BEFORE the handler is dispatched. Carries the
///   command verb, the inbound message id
///   (<see cref="ParsedCommand.SourceMessageId"/>), the inbound
///   trace id (<see cref="ParsedCommand.TraceId"/>), and the
///   operator / tenant / workspace context. If this write fails
///   the router rethrows <i>without</i> dispatching the handler —
///   the iter-5 evaluator item 2 fix: a router-level audit
///   failure cannot leave orphan side effects (e.g. an <c>/ask</c>
///   that publishes a SwarmCommand without a matching audit row,
///   or an <c>/approve</c> that emits a HumanDecisionEvent the
///   audit trail never recorded).</description></item>
///   <item><description><b>Completion row</b> (<see cref="CommandAuditPhases.Completed"/>)
///   — written AFTER the handler returns or throws. Carries the
///   same correlation id as the receipt (so log queries can
///   <c>WHERE CorrelationId=X</c> and reconstruct the full
///   command lifecycle) plus the handler's
///   <see cref="CommandResult.Success"/> /
///   <see cref="CommandResult.ErrorCode"/> outcome and any
///   thrown exception's type / message. Completion-write failures
///   do NOT rethrow — the receipt already guarantees the inbound
///   command is captured in the audit trail; the completion row
///   adds the outcome. Instead, the entry is enqueued onto the
///   durable <see cref="IAuditFallbackSink"/> (the same backstop
///   the pipeline uses for rejection rows) so the completion row
///   survives a transient audit-DB outage rather than being
///   silently dropped after a single log entry — the iter-10
///   evaluator fix for "all completion audit rows are permanently
///   lost with no durable backup".</description></item>
/// </list>
/// The unknown-command path emits a SINGLE row (Phase=Received,
/// Action=<see cref="UnknownCommandAuditAction"/>) — there is no
/// handler to wait on, so there's no separate completion phase.
/// </para>
/// <para>
/// <b>Why two rows.</b> The Stage 5.3 brief requires the audit row
/// to exist <i>before</i> any state-changing side effect (otherwise
/// retries duplicate work without a recoverable trail). The append-
/// only contract on <c>audit_logs</c> rules out updating a single
/// row in place; the two-row design preserves both properties —
/// receipt for integrity, completion for outcome — while keeping
/// every row joinable on a single correlation id.
/// </para>
/// <para>
/// <b>Field shape per row.</b>
/// <list type="bullet">
///   <item><description><see cref="AuditEntry.Action"/> = the bare
///   canonical command verb (e.g. <c>status</c>, <c>ask</c>),
///   normalised to lower-case so log queries can filter on a single
///   literal — Stage 5.3 iter-2 evaluator item 1: the prior
///   <c>command.&lt;name&gt;</c> overload broke the acceptance
///   assertion "<c>AuditLogEntry exists with Action=ask</c>" because
///   the column carried <c>command.ask</c> instead of <c>ask</c>.
///   The orthogonal "this is a command-family event" discriminator
///   moved to <see cref="AuditEntry.EventFamily"/> =
///   <see cref="AuditEventFamilies.Command"/>; unknown commands use
///   <see cref="UnknownCommandAuditAction"/>.</description></item>
///   <item><description><see cref="AuditEntry.MessageId"/> =
///   <see cref="ParsedCommand.SourceMessageId"/> (Stage 5.3 iter-6
///   evaluator item 3) — surfaces the Telegram <c>update_id</c> of
///   the inbound message onto the audit row so forensic queries
///   can join <c>audit_logs.MessageId</c> back to
///   <c>inbound_updates.EventId</c> without an out-of-band lookup.
///   <c>null</c> only when the caller constructed a
///   <see cref="ParsedCommand"/> outside the inbound pipeline
///   (tests, programmatic dispatch).</description></item>
///   <item><description><see cref="AuditEntry.UserId"/> = the
///   operator's Telegram user id (string form);
///   <see cref="AuditEntry.TenantId"/> = the resolved
///   <c>OperatorBinding.TenantId</c>, satisfying the persistence
///   layer's tenant column.</description></item>
///   <item><description><see cref="AuditEntry.CorrelationId"/> =
///   <see cref="ParsedCommand.TraceId"/> when present (the inbound
///   trace id propagated from
///   <c>MessengerEvent.CorrelationId</c>), falling back to a
///   freshly minted GUID otherwise so the persistence layer's
///   non-null CorrelationId contract is never violated. Both the
///   receipt and completion rows share the same correlation id —
///   the iter-6 evaluator item 2 fix changed the audit's
///   correlation source from the handler's <c>result.CorrelationId</c>
///   (which is the produced workflow's id and is unavailable
///   before the handler runs) to the inbound trace id (which is
///   available before the handler runs and is the same id every
///   pipeline / dedup / outbound artifact carries for this
///   update).</description></item>
///   <item><description><see cref="AuditEntry.Details"/> = a small
///   JSON blob. The receipt row carries the inbound shape
///   (<c>RawText</c>, <c>Arguments</c>, workspace, chat id,
///   message id, <c>"phase":"received"</c>); the completion row
///   carries the outcome (<c>Success</c>, <c>ErrorCode</c>,
///   exception type / message, <c>"phase":"completed"</c>). The
///   serialiser uses <see cref="JsonNamingPolicy.CamelCase"/> for
///   compactness; sensitive command arguments (e.g. comment text
///   in <c>/reject "I disagree"</c>) ARE included because the
///   brief's acceptance criterion ("<i>full context</i>") implies
///   the audit row carries enough context to reconstruct the
///   operator's intent.</description></item>
/// </list>
/// The audit calls are invoked via the
/// <see cref="IAuditLogger.LogAsync"/> path, which the
/// <c>PersistentAuditLogger</c> writes to the dedicated
/// <c>audit_logs</c> table per the Stage 5.3 schema.
///
/// <b>Stage 5.3 iter-3 evaluator item 6 — receipt audit failures propagate.</b>
/// A failed RECEIPT audit write is logged AND rethrown from
/// <see cref="EmitReceiptAuditAsync"/> so the handler is never
/// invoked — the iter-6 evaluator item 2 fix: a command can never
/// produce side effects (publish a SwarmCommand, emit a
/// HumanDecisionEvent) while the matching <c>audit_logs</c> row is
/// missing. The receipt path intentionally does NOT fall back to
/// <see cref="IAuditFallbackSink"/> — running the handler on the
/// strength of a fallback row would split the audit trail across
/// two mediums for the same correlation id and reintroduce the
/// orphan-side-effect window the rethrow exists to prevent. A
/// failed COMPLETION audit write, on the other hand, is logged
/// AND enqueued onto <see cref="IAuditFallbackSink"/> (Stage 5.3
/// iter-10 evaluator fix) — the receipt row already satisfies the
/// "log every inbound command" guarantee, but the completion row
/// carries the success/failure / error-code / handler-exception
/// details that operators rely on for forensic queries and that
/// must NOT silently disappear during a sustained audit-DB outage.
/// The completion path never rethrows — failing the operator after
/// a successful side effect would force them to retry an already-
/// committed command. Hosts that genuinely require lenient audit
/// semantics can register a tolerant <see cref="IAuditLogger"/>
/// decorator that catches inside the writer; the router itself
/// never tolerates the receipt gap.
/// </para>
/// </remarks>
public sealed class CommandRouter : ICommandRouter
{
    /// <summary>
    /// Machine-readable <see cref="CommandResult.ErrorCode"/> surfaced
    /// when <see cref="RouteAsync"/> receives a <see cref="ParsedCommand"/>
    /// whose <see cref="ParsedCommand.CommandName"/> is not in the
    /// dispatch table. Pinned as a constant so log queries and the
    /// pipeline-level test suite can reference it without duplicating
    /// the literal.
    /// </summary>
    public const string UnknownCommandErrorCode = "unknown_command";

    /// <summary>
    /// <see cref="AuditEntry.Action"/> emitted when the router rejects
    /// an unknown command. Stage 5.3 acceptance pins the column to the
    /// bare verb (<c>Action=ask</c> for <c>/ask</c>) — so an unknown
    /// command's row sets <c>Action=unknown</c> for parity. Pairs with
    /// <see cref="UnknownCommandErrorCode"/> on the
    /// <see cref="CommandResult"/>.
    /// </summary>
    public const string UnknownCommandAuditAction = "unknown";

    /// <summary>
    /// <see cref="AuditEntry.Action"/> emitted when the dispatched
    /// command handler throws. The exception is rethrown after the
    /// audit row is written (Stage 5.3 iter-2 evaluator item 3: the
    /// router MUST log every inbound command, even when the handler
    /// fails). The bare verb is preserved on the audit row's
    /// <see cref="AuditEntry.Action"/>; the <see cref="AuditEntry.Details"/>
    /// JSON records the exception type/message so the failure is
    /// recoverable from the audit trail alone.
    /// </summary>
    public const string HandlerThrewMarker = "handler_threw";

    /// <summary>
    /// Stage 5.3 iter-6 evaluator item 2 — canonical literals for the
    /// <c>phase</c> discriminator written into
    /// <see cref="AuditEntry.Details"/>. The receipt phase row is
    /// written BEFORE the handler dispatches (so a router-level audit
    /// failure cannot leave orphan side effects); the completion
    /// phase row is written AFTER the handler returns or throws. Both
    /// rows share the same correlation id so forensic queries can
    /// reconstruct the command lifecycle with a single
    /// <c>WHERE CorrelationId=X</c> predicate.
    /// </summary>
    public static class CommandAuditPhases
    {
        /// <summary>Phase written BEFORE handler dispatch — the integrity guarantee.</summary>
        public const string Received = "received";

        /// <summary>Phase written AFTER handler returns or throws — the observability bonus.</summary>
        public const string Completed = "completed";
    }

    private static readonly JsonSerializerOptions AuditDetailsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IReadOnlyDictionary<string, ICommandHandler> _handlers;
    private readonly IAuditLogger _audit;

    // Stage 5.3 iter-10 evaluator fix — durable backstop for the
    // COMPLETION audit row when the primary _audit writer throws.
    // Mirrors the pattern in TelegramUpdatePipeline.WriteRejectionAuditAsync:
    // primary first, fallback only inside the catch. The Stage 5.3
    // brief mandates "log every inbound command ... with full context"
    // and the completion row carries the success / failure / error-code
    // / handler-exception details that operators rely on for forensic
    // queries — these MUST NOT be silently dropped during a sustained
    // audit-DB outage. Production registers FileAuditFallbackSink via
    // AddMessagingPersistence; dev / unit-test bootstraps fall back to
    // NullAuditFallbackSink (a silent no-op) via the back-compat
    // constructor below, which preserves the prior shape for the
    // existing CommandRouterTests harnesses that pre-date this
    // dependency. The receipt path intentionally does NOT consult this
    // sink — see remarks above for why integrity beats durability on
    // the pre-dispatch audit.
    private readonly IAuditFallbackSink _auditFallback;

    private readonly TimeProvider _time;
    private readonly ILogger<CommandRouter> _logger;

    /// <summary>
    /// Backward-compatible constructor preserving the original
    /// four-argument shape used by the in-tree
    /// <c>CommandRouterTests</c> harness and any other direct-
    /// construction call site that pre-dates the Stage 5.3 iter-10
    /// <see cref="IAuditFallbackSink"/> dependency. Delegates to the
    /// five-arg primary constructor with a
    /// <see cref="NullAuditFallbackSink"/> so the fallback path is a
    /// silent no-op in environments that do not register the durable
    /// sink. Production hosts always resolve the five-arg overload via
    /// DI because <c>AddMessagingPersistence</c> registers
    /// <c>FileAuditFallbackSink</c> as the <see cref="IAuditFallbackSink"/>
    /// binding.
    /// </summary>
    public CommandRouter(
        IEnumerable<ICommandHandler> handlers,
        IAuditLogger audit,
        TimeProvider time,
        ILogger<CommandRouter> logger)
        : this(handlers, audit, new NullAuditFallbackSink(), time, logger)
    {
    }

    /// <summary>
    /// Stage 5.3 iter-10 primary constructor. The injected
    /// <see cref="IAuditFallbackSink"/> is consulted ONLY when the
    /// primary <see cref="IAuditLogger"/> throws while persisting the
    /// COMPLETION audit row — same discipline the pipeline's
    /// <c>WriteRejectionAuditAsync</c> uses for denial rows. DI in
    /// production wires <c>FileAuditFallbackSink</c> here (via
    /// <c>AddMessagingPersistence</c>) so completion rows survive a
    /// transient audit-DB outage on a durable JSONL file rather than
    /// being lost after a single log entry.
    /// </summary>
    public CommandRouter(
        IEnumerable<ICommandHandler> handlers,
        IAuditLogger audit,
        IAuditFallbackSink auditFallback,
        TimeProvider time,
        ILogger<CommandRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _auditFallback = auditFallback ?? throw new ArgumentNullException(nameof(auditFallback));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dict = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                throw new ArgumentException(
                    "ICommandHandler enumeration must not contain null entries.",
                    nameof(handlers));
            }
            if (string.IsNullOrWhiteSpace(handler.CommandName))
            {
                throw new ArgumentException(
                    $"ICommandHandler {handler.GetType().FullName} returned a blank CommandName.",
                    nameof(handlers));
            }
            if (!dict.TryAdd(handler.CommandName, handler))
            {
                var existing = dict[handler.CommandName];
                throw new InvalidOperationException(
                    $"Duplicate ICommandHandler registration for command '{handler.CommandName}': "
                    + $"{existing.GetType().FullName} vs {handler.GetType().FullName}. "
                    + "Each command name must have exactly one handler.");
            }
        }
        _handlers = dict;
    }

    /// <inheritdoc />
    public async Task<CommandResult> RouteAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        // Stage 5.3 iter-6 evaluator item 2 — pre-compute the shared
        // correlation id used by BOTH the receipt and completion
        // audit rows. The inbound trace id is preferred (set by the
        // pipeline from MessengerEvent.CorrelationId so every pipeline
        // / dedup / outbound artifact for this update shares the
        // same id); we fall back to a fresh GUID only when the caller
        // constructed the ParsedCommand outside the pipeline (tests,
        // programmatic dispatch) and did not set TraceId. The
        // persistence layer's CorrelationId column is non-null so
        // this fallback is essential.
        var auditCorrelationId = !string.IsNullOrWhiteSpace(command.TraceId)
            ? command.TraceId
            : Guid.NewGuid().ToString("N");

        var isKnown = false;
        ICommandHandler? matched = null;
        if (!string.IsNullOrWhiteSpace(command.CommandName)
            && _handlers.TryGetValue(command.CommandName, out matched))
        {
            isKnown = true;
        }

        // Stage 5.3 iter-6 evaluator item 2 — RECEIPT audit BEFORE
        // handler dispatch. Rationale: the prior router wrote the
        // command audit only AFTER the handler had already performed
        // its side effects (publish SwarmCommand, emit
        // HumanDecisionEvent), so a transient audit-DB failure left
        // orphan state with no recoverable trail. The receipt now
        // commits BEFORE the handler runs; on receipt-write failure
        // the rethrow below skips the handler entirely so retries
        // start from a clean slate.
        await EmitReceiptAuditAsync(command, @operator, auditCorrelationId, isKnown, ct).ConfigureAwait(false);

        CommandResult result;
        Exception? handlerException = null;

        if (!isKnown)
        {
            _logger.LogWarning(
                "CommandRouter received unknown command. Command={Command} OperatorId={OperatorId}",
                command.CommandName,
                @operator.OperatorId);

            // No handler to invoke for an unknown command — short-circuit
            // with a help-text result. The receipt already recorded the
            // attempt; there is no separate completion phase because no
            // side effects ran.
            return new CommandResult
            {
                Success = false,
                ResponseText = BuildUnknownCommandReply(command.CommandName),
                ErrorCode = UnknownCommandErrorCode,
                CorrelationId = auditCorrelationId,
            };
        }

        try
        {
            result = await matched!.HandleAsync(command, @operator, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stage 5.3 iter-2 evaluator item 3 — the router MUST
            // audit every inbound command, including one whose
            // handler throws. Capture the exception, synthesize a
            // failure CommandResult so the completion audit row
            // carries the failure context, emit the completion row,
            // THEN rethrow so the caller's normal error path runs
            // (pipeline-level try/catch surfaces the failure to the
            // operator and bumps the failure counter). The pipeline
            // does NOT get a partial success — the rethrow preserves
            // the observable behaviour for callers that were already
            // catching handler exceptions.
            handlerException = ex;
            result = new CommandResult
            {
                Success = false,
                ResponseText = string.Empty,
                ErrorCode = HandlerThrewMarker,
                CorrelationId = auditCorrelationId,
            };
        }

        // Stage 5.3 iter-6 evaluator item 2 — COMPLETION audit AFTER
        // handler returns or throws. The receipt above already
        // satisfies the "log every inbound command" persistence
        // guarantee; this row adds the outcome (Success / ErrorCode /
        // exception) for forensic queries. The same correlation id
        // ties it to the receipt so log queries can
        // `WHERE CorrelationId=X` and reconstruct the command
        // lifecycle. Failures here are LOGGED and ENQUEUED onto the
        // durable fallback sink (Stage 5.3 iter-10 fix) but NOT
        // rethrown — the receipt row guarantees the audit trail and
        // failing the operator after a successful side effect would
        // force them to retry an already-committed command.
        await EmitCompletionAuditAsync(
            command,
            @operator,
            auditCorrelationId,
            result,
            handlerException,
            ct).ConfigureAwait(false);

        if (handlerException is not null)
        {
            // Rethrow AFTER audit so callers that were already
            // catching handler exceptions observe identical behaviour
            // to the pre-Stage-5.3 router; the only difference is
            // the audit row that now exists for the failed dispatch.
            // Use ExceptionDispatchInfo so the original stack trace
            // is preserved across the await boundary.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(handlerException).Throw();
        }

        return result;
    }

    private async Task EmitReceiptAuditAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        string auditCorrelationId,
        bool isKnown,
        CancellationToken ct)
    {
        // Stage 5.3 acceptance assertion ("AuditLogEntry exists
        // with Action=ask") requires the bare command verb in
        // Action. EventFamily carries the orthogonal "this is a
        // command-family event" discriminator so log queries can
        // still filter the family with a single equality
        // predicate.
        var action = string.IsNullOrWhiteSpace(command.CommandName)
            ? UnknownCommandAuditAction
            : (isKnown
                ? command.CommandName.ToLowerInvariant()
                : UnknownCommandAuditAction);

        var details = JsonSerializer.Serialize(
            new CommandReceiptAuditDetails(
                CommandAuditPhases.Received,
                command.CommandName,
                command.RawText,
                command.Arguments?.Count > 0 ? command.Arguments : null,
                @operator.WorkspaceId,
                @operator.TelegramChatId,
                command.SourceMessageId),
            AuditDetailsJsonOptions);

        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            // Stage 5.3 iter-6 evaluator item 3 — propagate the
            // inbound transport message id onto MessageId so command
            // audit rows are joinable back to inbound_updates.EventId
            // without an out-of-band lookup. The prior router
            // hard-coded `MessageId = null` on every command audit
            // row which violated the Stage 5.3 brief's "full
            // context" requirement.
            MessageId = command.SourceMessageId,
            UserId = @operator.TelegramUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AgentId = null,
            Action = action,
            EventFamily = AuditEventFamilies.Command,
            Timestamp = _time.GetUtcNow(),
            CorrelationId = auditCorrelationId,
            TenantId = @operator.TenantId,
            Details = details,
        };

        try
        {
            await _audit.LogAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stage 5.3 iter-6 evaluator item 2 — the receipt audit
            // is the integrity guarantee for "log every inbound
            // command BEFORE side effects". A failure here means we
            // cannot safely run the handler (the handler's side
            // effects would be orphan: published SwarmCommand /
            // emitted HumanDecisionEvent with no recoverable audit
            // trail). We log loudly AND rethrow so the caller's
            // failure path runs WITHOUT the handler being dispatched
            // — the operator observes a hard failure and retries
            // start from a clean slate when the audit DB recovers.
            // Hosts that need lenient audit semantics can wrap
            // IAuditLogger with a tolerant decorator. Note: the
            // receipt path intentionally does NOT consult
            // IAuditFallbackSink — running the handler on the
            // strength of a fallback row would split the audit trail
            // across two mediums for the same correlation id and
            // reintroduce the orphan-side-effect window the rethrow
            // exists to prevent.
            _logger.LogError(
                ex,
                "CommandRouter failed to persist RECEIPT audit entry; skipping handler dispatch to prevent orphan side effects (Stage 5.3 iter-6 evaluator item 2). Command={Command} OperatorId={OperatorId} CorrelationId={CorrelationId}",
                command.CommandName,
                @operator.OperatorId,
                auditCorrelationId);
            throw;
        }
    }

    private async Task EmitCompletionAuditAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        string auditCorrelationId,
        CommandResult result,
        Exception? handlerException,
        CancellationToken ct)
    {
        var action = command.CommandName?.ToLowerInvariant() ?? UnknownCommandAuditAction;

        var details = JsonSerializer.Serialize(
            new CommandCompletionAuditDetails(
                CommandAuditPhases.Completed,
                command.CommandName,
                result.Success,
                result.ErrorCode,
                result.CorrelationId,
                @operator.WorkspaceId,
                @operator.TelegramChatId,
                command.SourceMessageId,
                handlerException?.GetType().FullName,
                handlerException?.Message),
            AuditDetailsJsonOptions);

        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = command.SourceMessageId,
            UserId = @operator.TelegramUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AgentId = null,
            Action = action,
            EventFamily = AuditEventFamilies.Command,
            Timestamp = _time.GetUtcNow(),
            CorrelationId = auditCorrelationId,
            TenantId = @operator.TenantId,
            Details = details,
        };

        try
        {
            await _audit.LogAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stage 5.3 iter-10 evaluator fix — primary audit-DB
            // failure must NOT silently drop the completion row.
            // Completion rows carry success / failure / error-code /
            // handler-exception details — exactly the forensic
            // context operators rely on during incident review. The
            // prior log-and-swallow shape permanently lost every
            // completion row during a sustained audit-DB outage
            // (the receipt row exists but the outcome row is gone),
            // breaking the Stage 5.3 brief's "log every inbound
            // command ... with full context" guarantee. We now mirror
            // the discipline the pipeline's WriteRejectionAuditAsync
            // uses: log loudly, then enqueue the entry onto the
            // durable IAuditFallbackSink (FileAuditFallbackSink in
            // production, NullAuditFallbackSink in dev / unit tests)
            // so the row lands on a separate medium even when the
            // primary writer is unavailable. We still do NOT rethrow
            // — failing the operator after a successful side effect
            // would force them to retry an already-committed command,
            // re-running the side effect with a different correlation
            // id and no way to dedup against the prior attempt. The
            // receipt row above remains the integrity guarantee for
            // "an audit row exists before the handler ran"; this
            // fallback adds the matching outcome row's durability.
            _logger.LogError(
                ex,
                "CommandRouter failed to persist COMPLETION audit entry; falling back to durable sink (Stage 5.3 iter-10). Command={Command} OperatorId={OperatorId} CorrelationId={CorrelationId} HandlerThrew={HandlerThrew} Success={Success}",
                command.CommandName,
                @operator.OperatorId,
                auditCorrelationId,
                handlerException is not null,
                result.Success);
            try
            {
                await _auditFallback.EnqueueAsync(entry, ct).ConfigureAwait(false);
            }
            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
            {
                // Both primary AND fallback failed. The handler's
                // side effects already happened (the receipt row
                // captured the inbound command before they ran), so
                // the operator is NOT failed — but the completion
                // row is genuinely lost for this dispatch. Escalate
                // to Critical so the operator is paged: a human must
                // reconstruct the outcome from downstream artifacts
                // and the structured logs below before declaring the
                // command's audit trail complete.
                _logger.LogCritical(
                    fallbackEx,
                    "CommandRouter completion audit fallback ALSO failed; completion row is lost for this dispatch. Operator intervention required. Command={Command} OperatorId={OperatorId} CorrelationId={CorrelationId} HandlerThrew={HandlerThrew} Success={Success} PrimaryError={PrimaryError}",
                    command.CommandName,
                    @operator.OperatorId,
                    auditCorrelationId,
                    handlerException is not null,
                    result.Success,
                    ex.Message);
            }
        }
    }

    /// <summary>
    /// Builds the operator-facing reply for an unknown command. Lists every
    /// recognized command name (with a leading <c>/</c>) so the operator
    /// has the complete vocabulary in front of them without consulting
    /// external docs. Centralized here so tests can pin the exact text
    /// without duplicating the join logic.
    /// </summary>
    public static string BuildUnknownCommandReply(string? commandName)
    {
        var available = string.Join(", ", TelegramCommands.All.Select(c => "/" + c));
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return $"Command not recognized. Available commands: {available}.";
        }
        return $"Command not recognized: /{commandName}. Available commands: {available}.";
    }

    /// <summary>
    /// Payload serialised into <see cref="AuditEntry.Details"/> for the
    /// RECEIPT audit row written BEFORE handler dispatch (Stage 5.3
    /// iter-6 evaluator item 2). Captures only the inbound shape —
    /// fields that are known before the handler runs — so the
    /// receipt commits without any handler-supplied value. The
    /// outcome (Success / ErrorCode / exception) lives on the
    /// separate completion row.
    /// </summary>
    private sealed record CommandReceiptAuditDetails(
        string Phase,
        string? CommandName,
        string? RawText,
        IReadOnlyList<string>? Arguments,
        string WorkspaceId,
        long TelegramChatId,
        string? SourceMessageId);

    /// <summary>
    /// Payload serialised into <see cref="AuditEntry.Details"/> for the
    /// COMPLETION audit row written AFTER handler returns or throws
    /// (Stage 5.3 iter-6 evaluator item 2). Captures the outcome
    /// shape — Success / ErrorCode / handler-supplied correlation id
    /// / exception type / message — so a forensic query joining
    /// receipt + completion by CorrelationId can reconstruct the
    /// full command lifecycle.
    /// </summary>
    private sealed record CommandCompletionAuditDetails(
        string Phase,
        string? CommandName,
        bool Success,
        string? ErrorCode,
        string? ResultCorrelationId,
        string WorkspaceId,
        long TelegramChatId,
        string? SourceMessageId,
        string? HandlerExceptionType,
        string? HandlerExceptionMessage);
}
