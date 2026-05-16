using System.Collections.Concurrent;

namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// In-memory store that suppresses duplicate outbound <see cref="AgentSwarm.Messaging.Abstractions.MessengerMessage"/>
/// deliveries within a configurable window. Introduced in Stage 6.2 step 4 of
/// <c>implementation-plan.md</c>: "<c>SendMessageAsync</c> checks if a message with the
/// same <c>CorrelationId</c> + <c>DestinationId</c> was already sent within a configurable
/// window".
/// </summary>
/// <remarks>
/// <para>
/// Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed on
/// <c>(CorrelationId, DestinationId)</c>. Each value records the wall-clock instant the
/// tuple was first observed plus a <see cref="TaskCompletionSource{TResult}"/> that
/// resolves once the in-flight winner reports the terminal outcome of the enqueue
/// pipeline (<c>true</c> = committed, <c>false</c> = rolled back / failed). Entries older
/// than <see cref="OutboundDeduplicationOptions.Window"/> are pruned both lazily on
/// <see cref="Claim"/> and proactively by the
/// <see cref="OutboundDeduplicationEvictionService"/> background timer.
/// </para>
/// <para>
/// <b>Iter-3 evaluator fix #1 — in-flight coordination for concurrent racers.</b>
/// Previously <see cref="TryRegister"/> was a single-shot bool: the loser observed
/// <c>false</c> and returned <i>immediately</i>, treating the call as a successful no-op
/// even though the winner had not yet reached the actual outbox <c>EnqueueAsync</c>. If
/// the winner then threw (transient infrastructure failure) and rolled back the slot
/// via <see cref="Remove"/>, the loser's caller had already received a success-shaped
/// response and would never retry, dropping the send entirely. The canonical
/// <see cref="Claim"/> API now exposes a <see cref="ClaimResult.WinnerOutcomeTask"/>
/// that resolves <c>true</c> when the winner calls <see cref="Commit"/> and <c>false</c>
/// when the winner calls <see cref="Remove"/>. The decorator
/// (<see cref="OutboxBackedMessengerConnector"/>) awaits this task on the loser path
/// and either suppresses (on <c>true</c>) or retries as the new winner (on
/// <c>false</c>), guaranteeing that exactly one outbox row lands per
/// <c>(CorrelationId, DestinationId)</c> tuple within the window even when concurrent
/// racers and transient failures interact.
/// </para>
/// <para>
/// The legacy <see cref="TryRegister"/> bool overload is preserved as a thin wrapper
/// over <see cref="Claim"/> so callers (and tests) written against the iter-1 API
/// continue to compile and behave identically for their narrow synchronous use case.
/// </para>
/// </remarks>
public sealed class OutboundMessageDeduplicator
{
    private static readonly Task<bool> _committedTrueTask = Task.FromResult(true);

    private readonly ConcurrentDictionary<(string CorrelationId, string DestinationId), Entry> _entries =
        new(KeyComparer.Instance);
    private readonly OutboundDeduplicationOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Construct the deduplicator with default options and the system clock.</summary>
    public OutboundMessageDeduplicator()
        : this(new OutboundDeduplicationOptions(), TimeProvider.System)
    {
    }

    /// <summary>Construct the deduplicator with the supplied options and clock.</summary>
    public OutboundMessageDeduplicator(OutboundDeduplicationOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (options.Window <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(OutboundDeduplicationOptions.Window)} must be strictly positive; got {options.Window}.",
                nameof(options));
        }

        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>Current entry count — used by tests and observability.</summary>
    public int Count => _entries.Count;

    /// <summary>Configured deduplication window — exposed so the eviction service can re-use the same value.</summary>
    public TimeSpan Window => _options.Window;

    /// <summary>
    /// Attempt to claim the dedupe slot for the supplied <paramref name="correlationId"/>
    /// + <paramref name="destinationId"/> pair. Returns a <see cref="ClaimResult"/>
    /// capturing whether the caller now owns the slot
    /// (<see cref="ClaimResult.IsOwner"/> = <c>true</c>) — in which case they must run
    /// the enqueue pipeline and report the terminal outcome via <see cref="Commit"/> on
    /// success or <see cref="Remove"/> on failure — or whether another caller already
    /// owns the slot, in which case <see cref="ClaimResult.WinnerOutcomeTask"/>
    /// resolves to <c>true</c> when the prior caller commits successfully (the new
    /// caller should suppress the duplicate send) or <c>false</c> when the prior caller
    /// rolls back (the new caller should re-claim and retry the pipeline as the new
    /// owner).
    /// </summary>
    /// <remarks>
    /// Lazily evicts the targeted entry if it has already aged past the window — this
    /// keeps a quiet system from observing false duplicates after a long pause and
    /// complements the proactive background eviction in
    /// <see cref="OutboundDeduplicationEvictionService"/>. Stale-entry waiters (if any
    /// — extreme edge case where a winner thread hung past the window without calling
    /// <see cref="Commit"/> or <see cref="Remove"/>) are signalled with <c>false</c> so
    /// they retry rather than treating the hung pipeline as a successful commit.
    /// </remarks>
    public ClaimResult Claim(string correlationId, string destinationId)
    {
        ValidateKey(correlationId, destinationId);
        var key = (correlationId, destinationId);

        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            var fresh = new Entry(now);
            if (_entries.TryAdd(key, fresh))
            {
                return new ClaimResult(IsOwner: true, WinnerOutcomeTask: _committedTrueTask);
            }

            if (_entries.TryGetValue(key, out var existing))
            {
                if (now - existing.RegisteredAt > _options.Window)
                {
                    // Stale entry — best-effort eviction so the next iteration treats
                    // the slot as free and the caller becomes the new owner. Signal
                    // any in-flight waiters with `false` so they retry rather than
                    // suppress under the assumption that a hung winner succeeded.
                    if (_entries.TryRemove(new KeyValuePair<(string, string), Entry>(key, existing)))
                    {
                        existing.CompletionSource.TrySetResult(false);
                    }
                    continue;
                }

                return new ClaimResult(IsOwner: false, WinnerOutcomeTask: existing.CompletionSource.Task);
            }

            // Race: between TryAdd failing and TryGetValue, another caller removed the
            // entry (Remove or EvictExpired). Loop and re-claim.
        }
    }

    /// <summary>
    /// Synchronous best-effort overload preserved for callers (and tests) written
    /// against the iter-1 API. Returns <c>true</c> when the caller owns the slot;
    /// returns <c>false</c> when an entry already exists. The caller does <i>not</i>
    /// learn whether the in-flight winner ultimately committed or rolled back — for
    /// that coordination, use <see cref="Claim"/> directly and await
    /// <see cref="ClaimResult.WinnerOutcomeTask"/>.
    /// </summary>
    public bool TryRegister(string correlationId, string destinationId)
        => Claim(correlationId, destinationId).IsOwner;

    /// <summary>
    /// Signal that the owning caller's enqueue pipeline completed successfully. The
    /// dedupe slot remains in the dictionary until natural eviction so future callers
    /// within the window are suppressed. Concurrent waiters observing
    /// <see cref="ClaimResult.WinnerOutcomeTask"/> see this commit as <c>true</c> and
    /// suppress their own enqueue. Idempotent — re-invoking is a no-op once the TCS is
    /// already completed.
    /// </summary>
    public void Commit(string correlationId, string destinationId)
    {
        ValidateKey(correlationId, destinationId);
        if (_entries.TryGetValue((correlationId, destinationId), out var existing))
        {
            existing.CompletionSource.TrySetResult(true);
        }
    }

    /// <summary>
    /// Roll back a prior <see cref="Claim"/> registration for the same
    /// <paramref name="correlationId"/> + <paramref name="destinationId"/> pair. Used by
    /// <see cref="OutboxBackedMessengerConnector.SendMessageAsync"/> when the post-claim
    /// pipeline (reference lookup, outbox enqueue) throws — without rollback a transient
    /// infrastructure failure would poison the dedupe window for
    /// <see cref="Window"/> and silently suppress every retry until the entry naturally
    /// expires. Concurrent waiters observing the removed entry's
    /// <see cref="ClaimResult.WinnerOutcomeTask"/> resolve to <c>false</c>, signalling
    /// that they should re-claim and run the pipeline themselves.
    /// </summary>
    /// <returns><c>true</c> if an entry was removed; <c>false</c> if no entry existed.</returns>
    public bool Remove(string correlationId, string destinationId)
    {
        ValidateKey(correlationId, destinationId);
        if (_entries.TryRemove((correlationId, destinationId), out var existing))
        {
            existing.CompletionSource.TrySetResult(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Purge every entry whose age exceeds <see cref="OutboundDeduplicationOptions.Window"/>
    /// as of <paramref name="now"/>. Returns the number of entries removed. Waiters on
    /// expired entries (extreme corner case) are signalled with <c>false</c> so they
    /// retry rather than suppress under the assumption that a hung winner succeeded.
    /// </summary>
    public int EvictExpired(DateTimeOffset now)
    {
        if (_entries.IsEmpty)
        {
            return 0;
        }

        var removed = 0;
        foreach (var kvp in _entries)
        {
            if (now - kvp.Value.RegisteredAt > _options.Window)
            {
                if (_entries.TryRemove(new KeyValuePair<(string, string), Entry>(kvp.Key, kvp.Value)))
                {
                    kvp.Value.CompletionSource.TrySetResult(false);
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>Purge expired entries using the injected clock's current instant.</summary>
    public int EvictExpired() => EvictExpired(_timeProvider.GetUtcNow());

    private static void ValidateKey(string correlationId, string destinationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            throw new ArgumentException("CorrelationId must be a non-empty string.", nameof(correlationId));
        }

        if (string.IsNullOrEmpty(destinationId))
        {
            throw new ArgumentException("DestinationId must be a non-empty string.", nameof(destinationId));
        }
    }

    /// <summary>
    /// Outcome of a <see cref="Claim"/> attempt. When <see cref="IsOwner"/> is
    /// <c>true</c>, the caller owns the slot and must report the terminal outcome via
    /// <see cref="Commit"/> on success or <see cref="Remove"/> on failure. When
    /// <c>false</c>, the caller awaits <see cref="WinnerOutcomeTask"/>: a <c>true</c>
    /// result means the prior caller committed successfully and the duplicate should
    /// be suppressed; a <c>false</c> result means the prior caller rolled back and the
    /// loser must re-claim and run the pipeline themselves.
    /// </summary>
    public readonly record struct ClaimResult(bool IsOwner, Task<bool> WinnerOutcomeTask);

    private sealed class Entry
    {
        public DateTimeOffset RegisteredAt { get; }
        public TaskCompletionSource<bool> CompletionSource { get; }

        public Entry(DateTimeOffset registeredAt)
        {
            RegisteredAt = registeredAt;
            CompletionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(string CorrelationId, string DestinationId)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string CorrelationId, string DestinationId) x, (string CorrelationId, string DestinationId) y)
            => StringComparer.Ordinal.Equals(x.CorrelationId, y.CorrelationId)
            && StringComparer.Ordinal.Equals(x.DestinationId, y.DestinationId);

        public int GetHashCode((string CorrelationId, string DestinationId) obj)
            => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.CorrelationId),
                StringComparer.Ordinal.GetHashCode(obj.DestinationId));
    }
}
