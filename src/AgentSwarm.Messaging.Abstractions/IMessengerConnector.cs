namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Platform-agnostic contract implemented by each messenger connector (Teams, Slack, Discord,
/// Telegram). Bridges the agent-swarm orchestrator to a concrete chat platform via the three
/// canonical operations described in <c>implementation-plan.md</c> §1.2.
/// </summary>
/// <remarks>
/// <para>
/// All three methods follow the standard <see cref="Task"/> / <see cref="Task{TResult}"/> async
/// .NET convention. Cancellation tokens are required so callers (hosted services, background
/// workers) can co-operate with graceful shutdown.
/// </para>
/// <para>
/// <see cref="ReceiveAsync"/> returns the next inbound <see cref="MessengerEvent"/> as
/// produced by the underlying connector. The Teams implementation (Stage 2.3) is backed by a
/// <c>System.Threading.Channels.Channel&lt;MessengerEvent&gt;</c> whose writer is fed by
/// <see cref="IInboundEventPublisher"/>; <see cref="ReceiveAsync"/> reads from the channel
/// reader and completes when an event is available.
/// </para>
/// </remarks>
public interface IMessengerConnector
{
    /// <summary>
    /// Send an outbound <see cref="MessengerMessage"/> to the target conversation.
    /// </summary>
    /// <param name="message">The platform-agnostic outbound message.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>A task that completes once the message has been enqueued for delivery.</returns>
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);

    /// <summary>
    /// Send an <see cref="AgentQuestion"/> proactively to the target user or channel. The
    /// connector is responsible for rendering the question into the messenger's native card
    /// format (Adaptive Card for Teams) and capturing the resulting conversation/activity IDs
    /// for subsequent update/delete operations.
    /// </summary>
    /// <param name="question">The agent question to deliver.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>A task that completes once the question has been enqueued for delivery.</returns>
    Task SendQuestionAsync(AgentQuestion question, CancellationToken ct);

    /// <summary>
    /// Read the next inbound <see cref="MessengerEvent"/> from the connector's event stream.
    /// </summary>
    /// <param name="ct">Cancellation token used to stop waiting for events.</param>
    /// <returns>
    /// A task that completes with the next <see cref="MessengerEvent"/> (one of
    /// <see cref="CommandEvent"/>, <see cref="DecisionEvent"/>, or <see cref="TextEvent"/>).
    /// Callers typically loop on this method inside a background worker.
    /// </returns>
    Task<MessengerEvent> ReceiveAsync(CancellationToken ct);
}
