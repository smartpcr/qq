using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Discord;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Sliding-window in-memory implementation of <see cref="IDeduplicationService"/>
/// backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/> per
/// architecture.md §4.11. Provides the fast-path duplicate suppression
/// layer: an entry remains "claimed" for the configured TTL (default 1
/// hour) after which it is eligible for eviction by the next operation.
/// The database UNIQUE constraint on
/// <see cref="DiscordInteractionRecord.InteractionId"/> (and equivalent per-
/// platform stores) is the cross-restart durability layer behind this cache
/// (architecture.md §10.2 layered dedup).
/// </summary>
/// <remarks>
/// <para>
/// Lazy eviction model: rather than spinning a timer (which would race with
/// disposal and complicate hosted-service registration), expired entries are
/// pruned on every <see cref="TryReserveAsync"/> /
/// <see cref="IsProcessedAsync"/> / <see cref="MarkProcessedAsync"/> call.
/// For a 100-agent fleet at the architectural limit, the dictionary stays
/// well under a few thousand keys per TTL window, so the inline scan is
/// negligible (single-digit microseconds).
/// </para>
/// <para>
/// Thread-safety: every operation uses
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>'s atomic primitives
/// (<see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/>,
/// <see cref="ConcurrentDictionary{TKey, TValue}.AddOrUpdate"/>), so
/// concurrent <see cref="TryReserveAsync"/> calls for the same id observe
/// the correct first-claim semantics.
/// </para>
/// </remarks>
public sealed class InMemoryDeduplicationService : IDeduplicationService
{
    /// <summary>Default sliding-window TTL (1 hour) per architecture.md §4.11.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _cache =
        new(StringComparer.Ordinal);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Creates a new dedup service with the default 1-hour TTL using
    /// <see cref="TimeProvider.System"/>.
    /// </summary>
    public InMemoryDeduplicationService()
        : this(TimeProvider.System, DefaultTtl)
    {
    }

    /// <summary>
    /// Creates a new dedup service with a custom clock and TTL. Used by tests
    /// (deterministic clock) and by hosts that need a longer dedup window
    /// for slow-running pipelines.
    /// </summary>
    /// <param name="timeProvider">Clock abstraction.</param>
    /// <param name="ttl">
    /// Sliding-window TTL. Must be strictly positive; entries older than
    /// <paramref name="ttl"/> are evicted on access.
    /// </param>
    public InMemoryDeduplicationService(TimeProvider timeProvider, TimeSpan ttl)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "ttl must be strictly positive.");
        }

        _ttl = ttl;
    }

    /// <inheritdoc />
    public Task<bool> TryReserveAsync(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        var now = _timeProvider.GetUtcNow();
        PruneExpired(now);

        // Atomic first-claim semantics: TryAdd succeeds only for the first
        // caller. Concurrent callers for the same id see false.
        var reserved = _cache.TryAdd(eventId, now);
        return Task.FromResult(reserved);
    }

    /// <inheritdoc />
    public Task<bool> IsProcessedAsync(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        var now = _timeProvider.GetUtcNow();
        PruneExpired(now);

        // "Observed within the dedup window" is the contract. An entry that
        // has not yet been pruned is in the window by definition.
        return Task.FromResult(_cache.ContainsKey(eventId));
    }

    /// <inheritdoc />
    public Task MarkProcessedAsync(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        var now = _timeProvider.GetUtcNow();
        PruneExpired(now);

        // Refresh the timestamp so the sliding window restarts from the
        // "processed" moment rather than the original reservation. This
        // matches the contract phrase "Idempotent in the marked-processed
        // terminal state" -- repeated calls extend the window.
        _cache[eventId] = now;
        return Task.CompletedTask;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        // ConcurrentDictionary enumerates a snapshot, so iterating while
        // other threads mutate is safe. The expiry decision and the value
        // we use for the conditional remove MUST come from the same
        // snapshot read -- otherwise a concurrent MarkProcessedAsync that
        // refreshes the entry between the snapshot read and the remove
        // would cause us to erase a freshly-claimed entry (false negative
        // on the next TryReserveAsync / IsProcessedAsync).
        //
        // We capture both key and timestamp from the snapshot enumerator
        // and pass the resulting KeyValuePair straight into the atomic
        // ConcurrentDictionary.TryRemove(KeyValuePair) overload -- which
        // only removes the entry when its current value equals the value
        // we observed. If a refresh happened mid-scan the current value
        // no longer matches, TryRemove returns false, and the entry stays
        // alive. This is the .NET 5+ atomic compare-and-remove primitive
        // and is the only race-free way to prune from a
        // ConcurrentDictionary.
        var threshold = now - _ttl;
        List<KeyValuePair<string, DateTimeOffset>>? expired = null;
        foreach (var kv in _cache)
        {
            if (kv.Value < threshold)
            {
                expired ??= new List<KeyValuePair<string, DateTimeOffset>>();
                expired.Add(kv);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var snapshot in expired)
        {
            _cache.TryRemove(snapshot);
        }
    }
}
