namespace Qq.Messaging.Abstractions;

/// <summary>
/// Routes parsed inbound commands to the appropriate handler in the agent swarm.
/// </summary>
public interface ICommandRouter
{
    Task RouteAsync(InboundCommand command, CancellationToken cancellationToken = default);
}
