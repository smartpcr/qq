namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
using System.Text;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles <c>/status</c>. Queries the swarm orchestrator for the
/// operator's workspace summary via
/// <see cref="ISwarmCommandBus.QueryStatusAsync"/> and returns a
/// formatted text reply. If the operator supplies a task id argument
/// (<c>/status TASK-99</c>), the query is narrowed via
/// <see cref="SwarmStatusQuery.TaskId"/>.
/// </summary>
public sealed class StatusCommandHandler : ICommandHandler
{
    private readonly ISwarmCommandBus _bus;
    private readonly ILogger<StatusCommandHandler> _logger;

    public StatusCommandHandler(
        ISwarmCommandBus bus,
        ILogger<StatusCommandHandler> logger)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string CommandName => TelegramCommands.Status;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        var taskId = command.Arguments.Count > 0 ? command.Arguments[0] : null;
        var query = new SwarmStatusQuery
        {
            WorkspaceId = @operator.WorkspaceId,
            TaskId = taskId,
        };

        _logger.LogInformation(
            "StatusCommandHandler querying swarm. WorkspaceId={WorkspaceId} TaskId={TaskId}",
            query.WorkspaceId,
            query.TaskId);

        var summary = await _bus.QueryStatusAsync(query, ct).ConfigureAwait(false);

        return new CommandResult
        {
            Success = true,
            ResponseText = FormatStatus(summary),
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>
    /// Renders <paramref name="summary"/> into a human-readable text block.
    /// Public so the unit tests can pin the exact formatting; the
    /// orchestrator may evolve <see cref="SwarmStatusSummary"/> over time
    /// and the formatting should remain stable.
    /// </summary>
    public static string FormatStatus(SwarmStatusSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Workspace {summary.WorkspaceId} — state: {summary.State}");
        if (!string.IsNullOrWhiteSpace(summary.TaskId))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nTask: {summary.TaskId}");
        }
        sb.Append(CultureInfo.InvariantCulture, $"\nActive agents: {summary.ActiveAgentCount}");
        sb.Append(CultureInfo.InvariantCulture, $"\nPending tasks: {summary.PendingTaskCount}");
        if (summary.LastActivityAt is not null)
        {
            sb.Append(
                CultureInfo.InvariantCulture,
                $"\nLast activity: {summary.LastActivityAt.Value.ToString("u", CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrWhiteSpace(summary.DisplayText))
        {
            sb.Append('\n').Append(summary.DisplayText);
        }
        return sb.ToString();
    }
}
