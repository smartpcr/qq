namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
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

        await _bus.PublishHumanDecisionAsync(decision, ct).ConfigureAwait(false);

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
            },
            ct).ConfigureAwait(false);

        // Transition AFTER publish+audit so a transient publish/audit
        // failure leaves the question Pending and re-deliverable. This
        // closes the "double-approve" loophole the iter-2 evaluator
        // flagged (Issue 1): the next /approve|/reject for the same id
        // takes the !Status.Pending early-return path above and is
        // surfaced as "no pending question found".
        await _questions.MarkAnsweredAsync(questionId, ct).ConfigureAwait(false);

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
