using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline.Stubs;

/// <summary>
/// Stage 2.2 in-memory stub <see cref="IDeduplicationService"/>. Backed by
/// two process-local <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// instances — one for "ever reserved" eventIds, one for the
/// fully-processed marker subset — so the pipeline can be wired
/// end-to-end before Stage 4.3 registers the distributed cache-backed
/// <c>DeduplicationService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>State separation.</b> Per architecture.md §4.9 and the
/// <see cref="IDeduplicationService"/> remarks, the reservation state
/// (set by <see cref="TryReserveAsync"/>) and the processed state (set
/// by <see cref="MarkProcessedAsync"/>) are <i>distinct</i>.
/// <see cref="IsProcessedAsync"/> reads the <c>_processed</c> bucket
/// only, so a reserved-but-not-completed event correctly probes as
/// <c>false</c> (the iter-1 evaluator item 4 distinction).
/// <c>_processed</c> is a strict subset of <c>_reservations</c>.
/// </para>
/// <para>
/// <b>Race-free reservation under concurrency.</b>
/// <see cref="TryReserveAsync"/> is a SINGLE atomic
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> on
/// <c>_reservations</c> — there is no probe step that could race against
/// <see cref="MarkProcessedAsync"/>. A concurrent burst of N callers
/// for the same event id sees exactly one winner; the rest return
/// <c>false</c> and short-circuit. To keep the gate closed even when
/// <see cref="MarkProcessedAsync"/> is invoked WITHOUT a prior
/// reservation (e.g. tooling-driven replay),
/// <see cref="MarkProcessedAsync"/> defensively writes BOTH buckets.
/// </para>
/// <para>
/// <b>Hybrid release-on-throw lifecycle (Stage 2.2 brief Scenario 4).</b>
/// <see cref="ReleaseReservationAsync"/> conditionally removes from
/// <c>_reservations</c> ONLY when the event id is NOT already in
/// <c>_processed</c>. This satisfies two competing properties:
/// <list type="bullet">
///   <item><description>Caught handler exception → pipeline calls
///   <see cref="ReleaseReservationAsync"/> → reservation removed → a
///   subsequent live re-delivery's <see cref="TryReserveAsync"/>
///   succeeds and the event is processed normally (the brief's
///   "subsequent delivery is processed normally" invariant).</description></item>
///   <item><description>Successful handler → pipeline calls
///   <see cref="MarkProcessedAsync"/> only (never
///   <see cref="ReleaseReservationAsync"/>) → reservation persists
///   alongside the processed marker → subsequent delivery short-
///   circuits at the dedup gate (the brief's "subsequent delivery is
///   short-circuited as duplicate" invariant). Even if a misbehaving
///   caller invokes <see cref="ReleaseReservationAsync"/> after
///   <see cref="MarkProcessedAsync"/>, the conditional check makes it
///   a no-op — the processed marker is sticky.</description></item>
///   <item><description>Handler returning <c>Success=false</c> →
///   pipeline calls <see cref="MarkProcessedAsync"/> as well (TERMINAL
///   semantics: throw = retryable, return = terminal). A subsequent
///   live re-delivery short-circuits at the dedup gate, and any
///   spurious release attempt is a no-op because the processed marker
///   has been written. Recovery for failed events is the operator's
///   choice (e.g. re-issuing the original command), not a live
///   webhook redelivery loop.</description></item>
/// </list>
/// The atomic-winner-per-concurrent-burst property holds because release
/// runs sequentially AFTER the winner's handler completes; there is no
/// window during the burst where two callers can both win.
/// </para>
/// <para>
/// <b>Process-local.</b> This stub is NOT cluster-safe — restart loses
/// both buckets, and there is no cross-pod coordination. The persistent
/// replacement in Stage 4.3 closes that gap (production sliding-window
/// cache evicts via TTL, not via active removal).
/// </para>
/// </remarks>
internal sealed class InMemoryDeduplicationService : IDeduplicationService
{
    private readonly ConcurrentDictionary<string, byte> _reservations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _processed = new(StringComparer.Ordinal);

    public Task<bool> TryReserveAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);
        // SINGLE atomic operation. Once an event id is in `_reservations`
        // — currently being processed, completed, or held while the
        // handler is in-flight — no concurrent caller can claim it.
        // No probe step ⇒ no probe-then-add race window. Subsequent
        // (post-handler-completion) callers may succeed only if a caught
        // handler exception triggered ReleaseReservationAsync.
        return Task.FromResult(_reservations.TryAdd(eventId, 0));
    }

    public Task ReleaseReservationAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);
        // Sticky-processed guard: if the event has already been marked
        // processed, the release is a NO-OP. This prevents a
        // misbehaving (or racing) release call from re-opening the gate
        // for an already-completed event — a successfully-processed
        // event must NEVER become re-runnable. The pipeline only calls
        // this on caught handler exceptions (where MarkProcessedAsync
        // was NOT called), so the guard is defense-in-depth.
        if (_processed.ContainsKey(eventId))
        {
            return Task.CompletedTask;
        }

        _reservations.TryRemove(eventId, out _);
        return Task.CompletedTask;
    }

    public Task<bool> IsProcessedAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);
        return Task.FromResult(_processed.ContainsKey(eventId));
    }

    public Task MarkProcessedAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);
        _processed.TryAdd(eventId, 0);
        // Defensively close the reservation gate. With a prior
        // TryReserveAsync this is a harmless no-op (TryAdd returns
        // false). Without a prior reservation (e.g. tooling-driven
        // replay), this guarantees a subsequent TryReserveAsync for the
        // same event id returns false — closing the iter-2 evaluator's
        // "duplicate caller can pass after MarkProcessedAsync" race.
        _reservations.TryAdd(eventId, 0);
        return Task.CompletedTask;
    }

    private static void ValidateEventId(string eventId)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            throw new ArgumentException("eventId must be non-null and non-empty.", nameof(eventId));
        }
    }
}
