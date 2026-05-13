namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Dispatches a parsed command to its handler in the context of a resolved
/// authorized operator. The inbound pipeline (Stage 2.2) parses and
/// authorizes first, then passes the resolved command and identity to the
/// router.
/// </summary>
public interface ICommandRouter
{
    Task<CommandResult> RouteAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct);
}
