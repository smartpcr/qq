namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Cross-platform deduplication contract used by every connector's inbound
/// pipeline to suppress duplicate platform deliveries (Discord interaction
/// retries, Telegram update redelivery, Slack at-least-once webhook semantics,
/// Teams replay). Three-method shape per architecture.md Section 4.11 and the
/// Stage 1.3 implementation-plan signature pinning.
/// </summary>
/// <remarks>
/// <para>
/// The <paramref name="eventId"/> argument is the platform-native unique event
/// identifier — Discord interaction snowflake stringified, Telegram update id,
/// Slack event id, Teams activity id. Implementations are expected to scope
/// their cache window per-platform when they share a backing store.
/// </para>
/// <para>
/// Recommended implementation pattern: an in-memory sliding-window cache
/// (default TTL 1 hour) for fast-path suppression, layered with a platform
/// store's UNIQUE constraint (e.g.
/// <see cref="IOutboundQueue.EnqueueAsync"/> on
/// <see cref="OutboundMessage.IdempotencyKey"/>) for cross-restart and
/// multi-instance protection.
/// </para>
/// <para>
/// The methods intentionally do NOT take a <see cref="CancellationToken"/>:
/// the implementation-plan pinned signatures as <c>TryReserveAsync(string)</c>,
/// <c>IsProcessedAsync(string)</c>, and <c>MarkProcessedAsync(string)</c>.
/// Dedup operations are expected to complete in single-digit milliseconds
/// against an in-memory cache or a local SQLite UNIQUE-constraint upsert; any
/// implementation that requires longer should enforce its own internal timeout
/// rather than threading cancellation through this contract.
/// </para>
/// </remarks>
public interface IDeduplicationService
{
    /// <summary>
    /// Atomically claims <paramref name="eventId"/> for processing. Returns
    /// <see langword="true"/> when the caller is the first claimant and may
    /// proceed; <see langword="false"/> when a previous reservation (in-flight
    /// or completed) is still within the dedup window.
    /// </summary>
    /// <param name="eventId">Platform-native event identifier.</param>
    Task<bool> TryReserveAsync(string eventId);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="eventId"/> has
    /// already been observed (either reserved or marked processed) within the
    /// current dedup window.
    /// </summary>
    /// <param name="eventId">Platform-native event identifier.</param>
    Task<bool> IsProcessedAsync(string eventId);

    /// <summary>
    /// Records that processing of <paramref name="eventId"/> finished. Idempotent
    /// in the marked-processed terminal state.
    /// </summary>
    /// <param name="eventId">Platform-native event identifier.</param>
    Task MarkProcessedAsync(string eventId);
}
