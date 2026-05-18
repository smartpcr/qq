namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared body for <c>/approve</c> and <c>/reject</c>. Both commands have
/// the same shape — first positional argument is the
/// <see cref="PendingQuestion.QuestionId"/>, behaviour is to load the
/// question, validate its <see cref="PendingQuestion.Status"/> and
/// route (chat + workspace match), emit a
/// <see cref="HumanDecisionEvent"/> with the command-specific
/// <see cref="HumanDecisionEvent.ActionValue"/>, persist a strongly-typed
/// <see cref="HumanResponseAuditEntry"/>, transition the question to
/// <see cref="PendingQuestionStatus.Answered"/> via
/// <see cref="IPendingQuestionStore.MarkAnsweredAsync"/>, and confirm to
/// the operator — so the two concrete handlers differ only in:
/// <list type="bullet">
///   <item>the canonical <see cref="HumanDecisionEvent.ActionValue"/>
///         they pass to the base; and</item>
///   <item>whether they consume a trailing free-text reason: <c>/reject</c>
///         carries optional reason text in
///         <see cref="HumanDecisionEvent.Comment"/> per architecture.md §5
///         (<c>/reject QUESTION-ID [reason]</c>); <c>/approve</c> does
///         not.</item>
/// </list>
/// Keeping the orchestration in one place makes the approve-vs-reject
/// contract a single edit away when the project evolves.
/// </summary>
public abstract class DecisionCommandHandlerBase : ICommandHandler
{
    /// <summary>
    /// Sentinel <see cref="HumanDecisionEvent.ExternalMessageId"/> prefix
    /// used when a decision originates from a typed slash command rather
    /// than a real Telegram callback. The Stage 3.3 callback handler
    /// uses the actual Telegram message id; here we synthesize
    /// <c>cmd:&lt;commandName&gt;:&lt;questionId&gt;</c> so audit consumers
    /// can distinguish the two provenance paths without needing extra
    /// fields on the event.
    /// </summary>
    public const string CommandOriginatedMessageIdPrefix = "cmd:";

    public const string MissingQuestionIdMessage =
        "Usage: `/{0} <questionId>` — supply the id of the question to {0}.";

    /// <summary>
    /// Surfaced when a question id does not resolve, is not in
    /// <see cref="PendingQuestionStatus.Pending"/>, or was routed to a
    /// different chat than the requesting operator. Same template for
    /// all three so an operator who guesses an id cannot tell whether
    /// the id exists in another workspace (info-leak resistance per
    /// architecture.md §4.3).
    /// </summary>
    public const string QuestionNotFoundTemplate =
        "❌ No pending question found for id `{0}`.";

    public const string ConfirmationTemplate =
        "✅ Question {0} {1}d.";

    private readonly IPendingQuestionStore _questions;
    private readonly ISwarmCommandBus _bus;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    protected DecisionCommandHandlerBase(
        IPendingQuestionStore questions,
        ISwarmCommandBus bus,
        IAuditLogger audit,
        TimeProvider time,
        ILogger logger)
    {
        _questions = questions ?? throw new ArgumentNullException(nameof(questions));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public abstract string CommandName { get; }

    /// <summary>
    /// Canonical <see cref="HumanAction.Value"/> the concrete handler
    /// represents (e.g. <c>"approve"</c>, <c>"reject"</c>). Carried into
    /// <see cref="HumanDecisionEvent.ActionValue"/> and
    /// <see cref="HumanResponseAuditEntry.ActionValue"/>.
    /// </summary>
    protected abstract string ActionValue { get; }

    /// <summary>
    /// <c>true</c> when the concrete handler honours an optional trailing
    /// free-text reason after the question id and propagates it as
    /// <see cref="HumanDecisionEvent.Comment"/> /
    /// <see cref="HumanResponseAuditEntry.Comment"/>. Only <c>/reject</c>
    /// does so today (architecture.md §5
    /// <c>/reject QUESTION-ID [reason]</c>); <c>/approve</c> overrides
    /// this to <c>false</c> and any trailing tokens are ignored.
    /// </summary>
    protected virtual bool AcceptsReason => false;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        if (command.Arguments.Count == 0
            || string.IsNullOrWhiteSpace(command.Arguments[0]))
        {
            return new CommandResult
            {
                Success = false,
                ResponseText = string.Format(
                    CultureInfo.InvariantCulture,
                    MissingQuestionIdMessage,
                    CommandName),
                ErrorCode = $"{CommandName}_missing_question_id",
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        var questionId = command.Arguments[0];

        // Optional trailing free-text reason. Joined with single spaces so
        // a multi-word reason ("not safe right now") survives the
        // whitespace-tokenized argument list produced by the parser.
        string? reason = null;
        if (AcceptsReason && command.Arguments.Count > 1)
        {
            var joined = string.Join(' ', command.Arguments.Skip(1)).Trim();
            if (!string.IsNullOrEmpty(joined))
            {
                reason = joined;
            }
        }

        var pending = await _questions.GetAsync(questionId, ct).ConfigureAwait(false);
        if (pending is null)
        {
            return NotFound(questionId, @operator, reasonLogged: "question_missing");
        }

        if (pending.Status != PendingQuestionStatus.Pending)
        {
            // Already answered, awaiting comment, or timed out — refuse to
            // re-emit a decision. Architecture.md §5 line 937 requires
            // Status == Pending before a HumanDecisionEvent fires.
            return NotFound(questionId, @operator, reasonLogged: $"status={pending.Status}");
        }

        if (pending.TelegramChatId != @operator.TelegramChatId)
        {
            // Cross-chat / cross-route attempt: the question was sent to
            // a different chat. Architecture.md §5 line 937 / 938
            // requires the requesting operator's authorized binding
            // (tenant/workspace via TelegramChatId) to match the
            // question's originating route before we emit a decision.
            // Same opaque "not found" surface so a hostile actor cannot
            // probe for valid question ids in other workspaces.
            return NotFound(questionId, @operator, reasonLogged: "chat_mismatch");
        }

        var receivedAt = _time.GetUtcNow();
        var telegramUserId = @operator.TelegramUserId.ToString(CultureInfo.InvariantCulture);

        // Stage 5.3 iter-3 evaluator item 7 — claim the row BEFORE
        // publish/audit so a concurrent QuestionTimeoutService sweep
        // (or a duplicate /approve|/reject delivery, or a button tap
        // from CallbackQueryHandler) cannot cause a double-decision.
        // MarkAnsweredAsync returns false on a lost claim; on that
        // path we surface the standard "no pending question" reply
        // and exit without publishing, mirroring the
        // !Status.Pending early-return above.
        var claimed = await _questions.MarkAnsweredAsync(questionId, ct).ConfigureAwait(false);
        if (!claimed)
        {
            return NotFound(questionId, @operator, reasonLogged: "lost_claim_race");
        }

        var decision = new HumanDecisionEvent
        {
            QuestionId = questionId,
            ActionValue = ActionValue,
            Comment = reason,
            Messenger = "telegram",
            ExternalUserId = telegramUserId,
            ExternalMessageId = CommandOriginatedMessageIdPrefix + CommandName + ":" + questionId,
            ReceivedAt = receivedAt,
            CorrelationId = pending.CorrelationId,
        };

        // Stage 5.3 iter-8 evaluator item 3 — AUDIT-FIRST ordering.
        // The publish runs AFTER the audit row is committed so that
        // a transient audit-DB failure CANNOT leak an outbound
        // HumanDecisionEvent without a durable audit_logs row. Prior
        // (iter-3..iter-7) ordering was publish-then-audit, which
        // left a window where the bus event escaped before the audit
        // row landed; the Stage 5.3 brief mandates "log every
        // outbound decision event with full context" and that
        // guarantee requires the audit row to land FIRST.
        //
        // Failure semantics:
        //   * Audit throws first  → no publish runs (clean retry —
        //     next /approve|/reject re-acquires the claim, audits,
        //     publishes exactly once).
        //   * Audit succeeds, publish throws → revert the claim so a
        //     retry can re-emit. The retry will land a SECOND audit
        //     row for the same decision; this is the documented
        //     persist-every-decision tradeoff (see
        //     QuestionTimeoutService remarks on at-least-once audit
        //     symmetry). Consumer-side dedup on QuestionId
        //     (architecture.md §10.3) absorbs the bounded duplicate
        //     publish.
        try
        {
            await _audit.LogHumanResponseAsync(
                new HumanResponseAuditEntry
                {
                    EntryId = Guid.NewGuid(),
                    MessageId = decision.ExternalMessageId,
                    UserId = telegramUserId,
                    AgentId = pending.AgentId,
                    QuestionId = questionId,
                    ActionValue = ActionValue,
                    Comment = reason,
                    Timestamp = receivedAt,
                    CorrelationId = pending.CorrelationId,
                    // Stage 5.3 iter-2 evaluator item 6 — populate tenant
                    // and workspace context on every decision audit row.
                    // The slash-command path already has an
                    // AuthorizedOperator so this is free; CallbackQueryHandler
                    // and QuestionTimeoutService pull TenantId from the
                    // PendingQuestion (which the connector stamps from the
                    // envelope's RoutingMetadata at StoreAsync time).
                    TenantId = @operator.TenantId,
                    Details = JsonSerializer.Serialize(
                        new DecisionAuditDetails(
                            @operator.WorkspaceId,
                            @operator.TelegramChatId,
                            @operator.OperatorAlias,
                            Source: CommandName,
                            TelegramMessageIdNumeric: null),
                        DecisionAuditDetailsContext.Default.DecisionAuditDetails),
                },
                ct).ConfigureAwait(false);

            await _bus.PublishHumanDecisionAsync(decision, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await _questions.TryRevertAnsweredClaimAsync(questionId, ct).ConfigureAwait(false);
            }
            catch (Exception revertEx) when (revertEx is not OperationCanceledException)
            {
                _logger.LogError(
                    revertEx,
                    "{Command}CommandHandler: atomic Answered claim revert threw; the original publish/audit exception will still propagate. QuestionId={QuestionId}",
                    CommandName,
                    questionId);
            }
            throw;
        }

        _logger.LogInformation(
            "{Command}CommandHandler emitted HumanDecisionEvent and marked answered. QuestionId={QuestionId} ActionValue={ActionValue} HasReason={HasReason} CorrelationId={CorrelationId}",
            CommandName,
            questionId,
            ActionValue,
            reason is not null,
            pending.CorrelationId);

        return new CommandResult
        {
            Success = true,
            ResponseText = string.Format(
                CultureInfo.InvariantCulture,
                ConfirmationTemplate,
                questionId,
                CommandName),
            CorrelationId = pending.CorrelationId,
        };
    }

    private CommandResult NotFound(string questionId, AuthorizedOperator @operator, string reasonLogged)
    {
        _logger.LogWarning(
            "{Command}CommandHandler refusing decision. QuestionId={QuestionId} OperatorId={OperatorId} Reason={Reason}",
            CommandName,
            questionId,
            @operator.OperatorId,
            reasonLogged);

        return new CommandResult
        {
            Success = false,
            ResponseText = string.Format(
                CultureInfo.InvariantCulture,
                QuestionNotFoundTemplate,
                questionId),
            ErrorCode = $"{CommandName}_question_not_found",
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }
}

/// <summary>Handles <c>/approve &lt;questionId&gt;</c>.</summary>
public sealed class ApproveCommandHandler : DecisionCommandHandlerBase
{
    public ApproveCommandHandler(
        IPendingQuestionStore questions,
        ISwarmCommandBus bus,
        IAuditLogger audit,
        TimeProvider time,
        ILogger<ApproveCommandHandler> logger)
        : base(questions, bus, audit, time, logger) { }

    public override string CommandName => TelegramCommands.Approve;

    protected override string ActionValue => SwarmCommandType.Approve;
}

/// <summary>Handles <c>/reject &lt;questionId&gt; [reason]</c>.</summary>
public sealed class RejectCommandHandler : DecisionCommandHandlerBase
{
    public RejectCommandHandler(
        IPendingQuestionStore questions,
        ISwarmCommandBus bus,
        IAuditLogger audit,
        TimeProvider time,
        ILogger<RejectCommandHandler> logger)
        : base(questions, bus, audit, time, logger) { }

    public override string CommandName => TelegramCommands.Reject;

    protected override string ActionValue => SwarmCommandType.Reject;

    /// <inheritdoc />
    /// <remarks>
    /// <c>/reject</c> uniquely accepts <c>[reason]</c> per architecture.md
    /// §5: the optional trailing text is carried verbatim as
    /// <see cref="HumanDecisionEvent.Comment"/> and
    /// <see cref="HumanResponseAuditEntry.Comment"/> so the rejecting
    /// agent and the audit log both retain the operator's stated reason.
    /// </remarks>
    protected override bool AcceptsReason => true;
}

/// <summary>
/// Strongly-typed payload behind
/// <see cref="HumanResponseAuditEntry.Details"/> for decision audit rows
/// emitted by <see cref="DecisionCommandHandlerBase"/> and the Telegram
/// callback / timeout paths. Captures the workspace context the
/// strongly-typed <see cref="HumanResponseAuditEntry.AgentId"/> /
/// <see cref="HumanResponseAuditEntry.QuestionId"/> /
/// <see cref="HumanResponseAuditEntry.ActionValue"/> columns do not
/// cover — the Stage 5.3 brief requires every decision row to carry
/// "full tenant/workspace context" (iter-2 evaluator item 6).
/// </summary>
/// <param name="WorkspaceId">
/// The operator's workspace identifier (architecture.md §3.1) when known.
/// </param>
/// <param name="TelegramChatId">
/// Chat the decision originated from / was rendered into. Always known.
/// </param>
/// <param name="OperatorAlias">
/// Display alias of the responding operator when known (slash-command
/// path); <see langword="null"/> for system-triggered decisions
/// (timeout sweep).
/// </param>
/// <param name="Source">
/// Provenance string — <c>approve</c> / <c>reject</c> for the slash-command
/// path, <c>callback</c> for an inline button press, <c>comment</c> for a
/// follow-up text reply, <c>timeout</c> for the
/// <see cref="AgentSwarm.Messaging.Telegram.QuestionTimeoutService"/>
/// sweep. Lets forensic queries pivot on the originating edge of the
/// decision without consulting <see cref="HumanResponseAuditEntry.MessageId"/>.
/// </param>
/// <param name="TelegramMessageIdNumeric">
/// Optional numeric Telegram <c>message_id</c> — populated by the
/// callback path so the row can be joined directly against the
/// rendered question without re-parsing <see cref="HumanResponseAuditEntry.MessageId"/>
/// (which is the synthesised <c>cmd:&lt;name&gt;:&lt;questionId&gt;</c>
/// string on the slash-command path).
/// </param>
public sealed record DecisionAuditDetails(
    string? WorkspaceId,
    long TelegramChatId,
    string? OperatorAlias,
    string Source,
    long? TelegramMessageIdNumeric);

/// <summary>
/// System.Text.Json source-generated context for
/// <see cref="DecisionAuditDetails"/>. Source-generated metadata avoids
/// the runtime-reflection path so the serializer is trim/AOT-safe should
/// the assembly ever be published with those flags.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(DecisionAuditDetails))]
public sealed partial class DecisionAuditDetailsContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
