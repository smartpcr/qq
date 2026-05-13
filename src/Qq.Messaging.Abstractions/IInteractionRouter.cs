namespace Qq.Messaging.Abstractions;

/// <summary>
/// Routes any inbound interaction (command or callback) to the appropriate handler.
/// </summary>
public interface IInteractionRouter
{
    Task RouteAsync(InboundInteraction interaction, CancellationToken cancellationToken = default);
}
