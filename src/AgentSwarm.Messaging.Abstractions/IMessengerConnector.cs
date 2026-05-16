namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Cross-platform contract implemented by every messenger connector
/// (Discord, Telegram, Slack, Teams). Owns the bidirectional plumbing between
/// the swarm orchestrator and the chosen chat platform: outbound text and
/// question deliveries, and inbound event drainage. See architecture.md
/// Section 4.1 and the FR-001 epic brief
/// (<c>.forge-attachments/agent_swarm_messenger_user_stories.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Concrete implementations are expected to delegate the durable side effects
/// (priority-aware outbound queue, idempotency, retry/dead-letter) to
/// <see cref="IOutboundQueue"/> and friends. <c>SendMessageAsync</c> /
/// <c>SendQuestionAsync</c> on the connector are <em>enqueue</em> operations
/// — the actual platform send happens later in
/// <see cref="IMessageSender"/> / the outbound queue processor — so callers
/// must not interpret a successful return as a delivered message.
/// </para>
/// <para>
/// <c>ReceiveAsync</c> drains already-pre-processed inbound events that the
/// connector's gateway/webhook pipeline has classified into
/// <see cref="MessengerEvent"/>s. Implementations should deduplicate at the
/// pipeline layer (see <see cref="IDeduplicationService"/>) so the events
/// returned here are guaranteed unique within their idempotency window.
/// </para>
/// </remarks>
public interface IMessengerConnector
{
    /// <summary>
    /// Enqueues an outbound generic message (status update, alert, command
    /// acknowledgement) for delivery to the messenger platform identified by
    /// <see cref="MessengerMessage.Messenger"/>.
    /// </summary>
    /// <param name="message">The pre-rendered outbound payload.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);

    /// <summary>
    /// Enqueues an agent question for delivery. The wrapped
    /// <see cref="AgentQuestionEnvelope"/> carries connector-specific routing
    /// keys (see <see cref="AgentQuestionEnvelope.RoutingMetadata"/>) and an
    /// optional default action hint. The connector renders the platform-native
    /// component shell (Discord embed + button row, Slack block kit, Telegram
    /// inline keyboard, etc.) at send time from the persisted envelope.
    /// </summary>
    /// <param name="envelope">The wrapped agent question + routing metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct);

    /// <summary>
    /// Drains the connector's processed inbound event buffer. Returns an empty
    /// list (never <see langword="null"/>) when no events are pending.
    /// Implementations should not block waiting for events — long-polling /
    /// gateway streaming happens elsewhere; this is a non-blocking pull.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct);
}
