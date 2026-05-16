namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared body for <c>/pause</c> and <c>/resume</c>. Both commands take
/// a single positional <see cref="SwarmCommand.TaskId"/> argument and
/// emit a <see cref="SwarmCommand"/> on <see cref="ISwarmCommandBus"/>
/// with the appropriate <see cref="SwarmCommand.CommandType"/>. Sharing
/// the orchestration here makes the contract a single edit away when
/// the team adds e.g. a comment-payload override or per-agent scope.
/// </summary>
public abstract class LifecycleCommandHandlerBase : ICommandHandler
{
    public const string MissingTaskIdMessage =
        "Usage: `/{0} <taskId>` — supply the id of the task to {0}.";

    public const string ConfirmationTemplate =
        "✅ {0} signal sent for task {1}. Correlation: {2}";

    private readonly ISwarmCommandBus _bus;
    private readonly ILogger _logger;

    protected LifecycleCommandHandlerBase(
        ISwarmCommandBus bus,
        ILogger logger)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public abstract string CommandName { get; }

    /// <summary>
    /// Canonical <see cref="SwarmCommand.CommandType"/> the concrete
    /// handler emits (e.g. <see cref="SwarmCommandType.Pause"/>).
    /// </summary>
    protected abstract string SwarmCommandTypeValue { get; }

    /// <summary>
    /// Display-friendly verb used in the operator-facing confirmation
    /// (e.g. <c>"Pause"</c>). Distinct from <see cref="CommandName"/>
    /// (which is lowercase) so the surfaced text reads naturally.
    /// </summary>
    protected abstract string DisplayVerb { get; }

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
                    MissingTaskIdMessage,
                    CommandName),
                ErrorCode = $"{CommandName}_missing_task_id",
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        var taskId = command.Arguments[0];
        var correlationId = Guid.NewGuid().ToString("N");
        var swarmCommand = new SwarmCommand
        {
            CommandType = SwarmCommandTypeValue,
            TaskId = taskId,
            OperatorId = @operator.OperatorId,
            CorrelationId = correlationId,
        };

        _logger.LogInformation(
            "{Command}CommandHandler publishing lifecycle signal. TaskId={TaskId} OperatorId={OperatorId} CorrelationId={CorrelationId}",
            CommandName,
            taskId,
            @operator.OperatorId,
            correlationId);

        await _bus.PublishCommandAsync(swarmCommand, ct).ConfigureAwait(false);

        return new CommandResult
        {
            Success = true,
            ResponseText = string.Format(
                CultureInfo.InvariantCulture,
                ConfirmationTemplate,
                DisplayVerb,
                taskId,
                correlationId),
            CorrelationId = correlationId,
        };
    }
}

/// <summary>Handles <c>/pause &lt;taskId&gt;</c>.</summary>
public sealed class PauseCommandHandler : LifecycleCommandHandlerBase
{
    public PauseCommandHandler(
        ISwarmCommandBus bus,
        ILogger<PauseCommandHandler> logger)
        : base(bus, logger) { }

    public override string CommandName => TelegramCommands.Pause;

    protected override string SwarmCommandTypeValue => SwarmCommandType.Pause;

    protected override string DisplayVerb => "Pause";
}

/// <summary>Handles <c>/resume &lt;taskId&gt;</c>.</summary>
public sealed class ResumeCommandHandler : LifecycleCommandHandlerBase
{
    public ResumeCommandHandler(
        ISwarmCommandBus bus,
        ILogger<ResumeCommandHandler> logger)
        : base(bus, logger) { }

    public override string CommandName => TelegramCommands.Resume;

    protected override string SwarmCommandTypeValue => SwarmCommandType.Resume;

    protected override string DisplayVerb => "Resume";
}
