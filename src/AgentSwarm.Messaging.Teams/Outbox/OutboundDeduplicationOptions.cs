namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// Options controlling the outbound-message deduplication window introduced in Stage 6.2
/// step 4 of <c>implementation-plan.md</c> (Duplicate Suppression and Idempotency).
/// </summary>
/// <remarks>
/// The deduplicator suppresses an outbound <see cref="AgentSwarm.Messaging.Abstractions.MessengerMessage"/>
/// whose <c>(CorrelationId, DestinationId)</c> tuple has already been observed within
/// <see cref="Window"/>. The window defaults to <c>10 minutes</c> — narrow enough that a
/// genuine user-initiated retry can succeed after a transient downstream failure, but wide
/// enough to absorb a within-tick orchestrator re-emission.
/// </remarks>
public sealed class OutboundDeduplicationOptions
{
    /// <summary>
    /// Time window during which a duplicate <c>(CorrelationId, DestinationId)</c> tuple
    /// is suppressed. Must be a strictly positive <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cadence at which expired entries are purged from the deduplicator's in-memory
    /// store. Defaults to <c>1 minute</c>. Must be strictly positive.
    /// </summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromMinutes(1);
}
