namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles <c>/ask &lt;body&gt;</c>. Constructs a synthesized task id,
/// publishes a <see cref="SwarmCommandType.CreateTask"/>
/// <see cref="SwarmCommand"/> on <see cref="ISwarmCommandBus"/> carrying
/// the operator-provided body text as the
/// <see cref="SwarmCommand.Payload"/>, and replies with the assigned
/// task id and correlation id so the operator can quote them in a
/// follow-up command (e.g. <c>/status TASK-...</c>) or in a support
/// ticket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Task id generation.</b> The id is synthesized client-side
/// (<c>TASK-XXXXXXXX</c>) so the connector can echo a stable identifier
/// to the operator immediately, even when the orchestrator's
/// <see cref="ISwarmCommandBus.PublishCommandAsync"/> is asynchronous /
/// fire-and-forget. The orchestrator is expected to honour the
/// supplied id (idempotency by <see cref="SwarmCommand.TaskId"/>); if
/// it rebrands the task, that is its responsibility, not ours.
/// </para>
/// <para>
/// <b>Acceptance criterion.</b> Story brief:
/// <i>"Human can send <c>/ask build release notes for Solution12</c>
/// and the swarm creates a work item."</i> Verified by
/// <c>AskCommandHandlerTests</c>.
/// </para>
/// </remarks>
public sealed class AskCommandHandler : ICommandHandler
{
    public const string MissingBodyMessage =
        "Usage: `/ask <task description>` — please describe the work to schedule.";

    public const string ConfirmationTemplate =
        "✅ Task {0} created. Correlation: {1}";

    private readonly ISwarmCommandBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<AskCommandHandler> _logger;

    public AskCommandHandler(
        ISwarmCommandBus bus,
        TimeProvider time,
        ILogger<AskCommandHandler> logger)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string CommandName => TelegramCommands.Ask;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        var body = string.Join(' ', command.Arguments).Trim();
        if (string.IsNullOrEmpty(body))
        {
            return new CommandResult
            {
                Success = false,
                ResponseText = MissingBodyMessage,
                ErrorCode = "ask_missing_body",
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        var taskId = GenerateTaskId();
        var correlationId = Guid.NewGuid().ToString("N");
        var swarmCommand = new SwarmCommand
        {
            CommandType = SwarmCommandType.CreateTask,
            TaskId = taskId,
            OperatorId = @operator.OperatorId,
            Payload = body,
            CorrelationId = correlationId,
        };

        _logger.LogInformation(
            "AskCommandHandler publishing CreateTask. TaskId={TaskId} OperatorId={OperatorId} CorrelationId={CorrelationId}",
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
                taskId,
                correlationId),
            CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// Generate a short, human-quotable task id. Format
    /// <c>TASK-XXXXXXXX</c> where the suffix is the first 8 hex chars of
    /// a fresh GUID — collision-resistant enough for the operator-facing
    /// identifier (~4 billion values per quadrillion population) while
    /// staying short enough to type in a follow-up command. Public so
    /// tests can pin the format.
    /// </summary>
    public static string GenerateTaskId()
        => "TASK-" + Guid.NewGuid().ToString("N").AsSpan(0, 8).ToString().ToUpperInvariant();
}
