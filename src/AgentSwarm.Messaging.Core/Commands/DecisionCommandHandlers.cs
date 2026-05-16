namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared body for <c>/approve</c> and <c>/reject</c>. Both commands have
/// the same shape — first positional argument is the
/// <see cref="PendingQuestion.QuestionId"/>, behaviour is to load the
/// question, emit a <see cref="HumanDecisionEvent"/> with the
/// command-specific <see cref="HumanDecisionEvent.ActionValue"/>, persist
/// a strongly-typed <see cref="HumanResponseAuditEntry"/>, and confirm
/// to the operator — so the two concrete handlers differ only in the
/// canonical <see cref="HumanDecisionEvent.ActionValue"/> they pass to
/// the base. Keeping the orchestration in one place makes the
/// approve-vs-reject contract a single edit away when the project
/// evolves.
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
        var pending = await _questions.GetAsync(questionId, ct).ConfigureAwait(false);
        if (pending is null)
        {
            _logger.LogWarning(
                "{Command}CommandHandler did not find pending question. QuestionId={QuestionId} OperatorId={OperatorId}",
                CommandName,
                questionId,
                @operator.OperatorId);

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

        var receivedAt = _time.GetUtcNow();
        var telegramUserId = @operator.TelegramUserId.ToString(CultureInfo.InvariantCulture);
        var decision = new HumanDecisionEvent
        {
            QuestionId = questionId,
            ActionValue = ActionValue,
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
                Timestamp = receivedAt,
                CorrelationId = pending.CorrelationId,
            },
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "{Command}CommandHandler emitted HumanDecisionEvent. QuestionId={QuestionId} ActionValue={ActionValue} CorrelationId={CorrelationId}",
            CommandName,
            questionId,
            ActionValue,
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

/// <summary>Handles <c>/reject &lt;questionId&gt;</c>.</summary>
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
}
