namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Fast in-pipeline deduplication acceleration layer above the canonical
/// <c>IInboundUpdateStore</c>. Backed by a sliding-window cache.
/// </summary>
/// <remarks>
/// <para>The interface exposes a primitive atomic reservation, a paired
/// release primitive used on caught handler exceptions, and a legacy
/// two-phase pair. Always prefer <see cref="TryReserveAsync"/> when
/// implementing duplicate suppression in the inbound pipeline — it is the
/// only operation that is safe against concurrent callers without a
/// downstream durable constraint. The legacy <see cref="IsProcessedAsync"/>
/// / <see cref="MarkProcessedAsync"/> pair is retained for read-only cache
/// probes and for code paths where atomicity is provided by
/// <c>IInboundUpdateStore.PersistAsync</c> (architecture.md §4.8); using
/// them as a check-then-act pair is racy and must not be done where
/// concurrent webhook deliveries could be in flight.</para>
/// <para><b>Reservation lifecycle (Stage 2.2 hybrid retry semantics).</b>
/// The pipeline calls <see cref="TryReserveAsync"/> first; on success it
/// owns the slot. On the success path it calls <see cref="MarkProcessedAsync"/>
/// after the handler completes; on a CAUGHT handler exception it calls
/// <see cref="ReleaseReservationAsync"/> so the next live re-delivery of
/// the same event id is processed normally (the brief's "subsequent
/// delivery is processed normally (not short-circuited as duplicate)"
/// invariant). On a handler that returns
/// <c>CommandResult.Success=false</c>, the pipeline ALSO calls
/// <see cref="MarkProcessedAsync"/> (NOT
/// <see cref="ReleaseReservationAsync"/>) — the contract is <i>throw =
/// retryable, return = terminal</i>, so a returned failure is treated
/// as a definitive operator response that should not be re-issued by a
/// live re-delivery. On an UNCAUGHT crash (process exits between
/// <c>TryReserveAsync</c> and the catch block) the reservation persists
/// because <see cref="ReleaseReservationAsync"/> never runs; the Stage
/// 2.4 <c>InboundUpdate</c> recovery sweep is the canonical recovery
/// route in that crash case (which closes the "two pods both run the
/// handler on a crash" race the implementation-plan deliberately
/// addresses while still satisfying the brief's live-retry-on-throw
/// scenario).</para>
/// <para>The acceptance criterion "Duplicate webhook delivery does not
/// execute the same human command twice" is satisfied by the combination of
/// <see cref="TryReserveAsync"/> in this interface and the <c>UNIQUE
/// (update_id)</c> constraint on <c>InboundUpdate</c>. Concurrent callers
/// always see a single winner — the hybrid release-on-throw path only
/// affects subsequent (post-handler-completion) deliveries.</para>
/// </remarks>
public interface IDeduplicationService
{
    /// <summary>
    /// Atomically attempts to claim the supplied <paramref name="eventId"/>.
    /// Returns <c>true</c> exactly once across all concurrent callers for the
    /// same id; returns <c>false</c> for every subsequent attempt UNTIL
    /// either (a) <see cref="ReleaseReservationAsync"/> releases the slot
    /// after a caught handler exception, in which case a fresh
    /// <see cref="TryReserveAsync"/> for the same id can succeed again, or
    /// (b) the underlying store evicts the entry by TTL. The implementation
    /// MUST perform the underlying check-and-set as a single atomic
    /// operation (e.g. <c>StringSet</c> with <c>When.NotExists</c> against
    /// Redis, conditional INSERT against SQL, or an
    /// <c>Interlocked.CompareExchange</c>-based in-memory store).
    /// </summary>
    Task<bool> TryReserveAsync(string eventId, CancellationToken ct);

    /// <summary>
    /// Releases a reservation previously acquired by
    /// <see cref="TryReserveAsync"/> when the routed handler throws a
    /// caught exception, so a subsequent live re-delivery of the same
    /// <paramref name="eventId"/> can be processed normally (Stage 2.2
    /// brief Scenario 4: "subsequent delivery of evt-1 is processed
    /// normally (not short-circuited as duplicate)"). MUST be a no-op
    /// when the event id has already been marked processed via
    /// <see cref="MarkProcessedAsync"/> — a successfully-processed
    /// event must NOT become re-runnable by an unexpected release call.
    /// MUST also be a no-op when the event id was never reserved.
    /// Implementations are expected to be idempotent.
    /// </summary>
    Task ReleaseReservationAsync(string eventId, CancellationToken ct);

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
    /// cache write-through. Once an event id is marked processed, a
    /// subsequent <see cref="ReleaseReservationAsync"/> for the same id
    /// MUST be a no-op (the processed marker is sticky and overrides
    /// any release attempt).
    /// </summary>
    Task MarkProcessedAsync(string eventId, CancellationToken ct);
}
