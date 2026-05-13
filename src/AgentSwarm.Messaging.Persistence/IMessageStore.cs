using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Persistent store for inbound and outbound messenger traffic. Provides write paths for
/// the gateway (inbound events received from a messenger, outbound messages produced by an
/// agent) and a correlation-keyed query for trace investigation and replay.
/// </summary>
/// <remarks>
/// Concrete implementations are introduced in later phases (the SQL implementation backs
/// the gateway's persistence database). Stage 1.3 defines the contract only — see
/// <c>tech-spec.md</c> §4.3 (Compliance) and the Stage 1.3 test scenarios for the canonical
/// requirements.
/// </remarks>
public interface IMessageStore
{
    /// <summary>
    /// Persist an inbound event received from a messenger connector. The supplied event is
    /// expected to be one of the canonical <see cref="MessengerEvent"/> subtypes
    /// (<see cref="CommandEvent"/>, <see cref="DecisionEvent"/>, <see cref="TextEvent"/>).
    /// </summary>
    /// <param name="inboundEvent">The inbound event to persist.</param>
    /// <param name="cancellationToken">Token observed by transport-layer I/O.</param>
    Task SaveInboundAsync(MessengerEvent inboundEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Persist an outbound message produced by an agent for delivery to a messenger.
    /// </summary>
    /// <param name="outboundMessage">The outbound message to persist.</param>
    /// <param name="cancellationToken">Token observed by transport-layer I/O.</param>
    Task SaveOutboundAsync(MessengerMessage outboundMessage, CancellationToken cancellationToken);

    /// <summary>
    /// Return every persisted message (inbound and outbound) that shares the supplied
    /// end-to-end trace ID, ordered by <see cref="PersistedMessage.Timestamp"/> ascending.
    /// </summary>
    /// <param name="correlationId">The correlation/trace ID to look up.</param>
    /// <param name="cancellationToken">Token observed by transport-layer I/O.</param>
    /// <returns>
    /// A snapshot list of matching <see cref="PersistedMessage"/> envelopes. Returns an
    /// empty list when no rows match.
    /// </returns>
    Task<IReadOnlyList<PersistedMessage>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken);
}
