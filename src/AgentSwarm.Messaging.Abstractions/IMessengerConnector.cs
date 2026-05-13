namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Shared abstraction implemented by every messenger connector (Telegram,
/// Discord, Slack, Teams). The connector translates inbound platform updates
/// into <see cref="MessengerEvent"/>s and delivers outbound messages and
/// questions originating from the agent swarm.
/// </summary>
/// <remarks>
/// Defined in <c>AgentSwarm.Messaging.Abstractions</c> per architecture.md
/// §4.1 and implementation-plan.md Stage 1.3.
/// </remarks>
public interface IMessengerConnector
{
    /// <summary>
    /// Enqueue an informational message for delivery to the target messenger.
    /// Implementations must be non-blocking and durable: the caller is
    /// returning to the agent swarm event loop, not awaiting transport
    /// completion.
    /// </summary>
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);

    /// <summary>
    /// Enqueue an agent question, including its routing/context sidecar
    /// metadata. The connector reads <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>
    /// and <see cref="AgentQuestionEnvelope.RoutingMetadata"/> (e.g.
    /// <c>TelegramChatId</c>) when rendering and persisting the question.
    /// </summary>
    Task SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct);

    /// <summary>
    /// Drain currently-buffered inbound events that have been mapped from the
    /// underlying messenger platform. Implementations may return an empty list
    /// when no events are pending.
    /// </summary>
    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct);
}
