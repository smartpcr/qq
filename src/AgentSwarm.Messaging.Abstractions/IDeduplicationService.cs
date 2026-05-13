namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Fast in-pipeline deduplication acceleration layer above the canonical
/// <c>IInboundUpdateStore</c>. Backed by a sliding-window cache.
/// </summary>
/// <remarks>
/// <para>The interface exposes both a primitive atomic operation and a
/// legacy two-phase pair. Always prefer <see cref="TryReserveAsync"/> when
/// implementing duplicate suppression in the inbound pipeline — it is the
/// only operation that is safe against concurrent callers without a
/// downstream durable constraint. The legacy <see cref="IsProcessedAsync"/>
/// / <see cref="MarkProcessedAsync"/> pair is retained for read-only cache
/// probes and for code paths where atomicity is provided by
/// <c>IInboundUpdateStore.PersistAsync</c> (architecture.md §4.8); using
/// them as a check-then-act pair is racy and must not be done where
/// concurrent webhook deliveries could be in flight.</para>
/// <para>The acceptance criterion "Duplicate webhook delivery does not
/// execute the same human command twice" is satisfied by the combination of
/// <see cref="TryReserveAsync"/> in this interface and the <c>UNIQUE
/// (update_id)</c> constraint on <c>InboundUpdate</c>.</para>
/// </remarks>
public interface IDeduplicationService
{
    /// <summary>
    /// Atomically attempts to claim the supplied <paramref name="eventId"/>.
    /// Returns <c>true</c> exactly once across all concurrent callers for the
    /// same id; returns <c>false</c> for every subsequent attempt. The
    /// implementation MUST perform the underlying check-and-set as a single
    /// atomic operation (e.g. <c>StringSet</c> with <c>When.NotExists</c>
    /// against Redis, conditional INSERT against SQL, or an
    /// <c>Interlocked.CompareExchange</c>-based in-memory store).
    /// </summary>
    Task<bool> TryReserveAsync(string eventId, CancellationToken ct);

    /// <summary>
    /// Non-atomic existence probe. Safe ONLY for diagnostic logging and
    /// short-circuit before <see cref="TryReserveAsync"/>. Do NOT use as the
    /// first half of a check-then-act dedup gate — that pattern races. See
    /// the type-level remarks above.
    /// </summary>
    Task<bool> IsProcessedAsync(string eventId, CancellationToken ct);

    /// <summary>
    /// Persists a processed marker without performing an atomic reservation.
    /// Retained for code paths where the upstream durable store (e.g.
    /// <c>IInboundUpdateStore.PersistAsync</c>) has already enforced
    /// uniqueness via a <c>UNIQUE</c> constraint and this call is purely a
    /// cache write-through.
    /// </summary>
    Task MarkProcessedAsync(string eventId, CancellationToken ct);
}
