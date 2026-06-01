// -----------------------------------------------------------------------
// <copyright file="SlackInProcessIdempotencyStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// In-process TTL-bounded dedup store used by the modal fast-path
/// (Stage 4.1) to suppress duplicate <c>/agent review</c> /
/// <c>/agent escalate</c> invocations within Slack's
/// <c>trigger_id</c> lifetime window. Backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> with explicit
/// expiry timestamps; expired entries are evicted lazily on the next
/// touch AND eagerly by a background scavenger so the dictionary
/// does not grow without bound under steady traffic.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 supplies the canonical <c>SlackIdempotencyGuard</c>
/// backed by the durable <c>slack_inbound_request_record</c> table.
/// The modal fast-path runs INSIDE the HTTP request lifecycle and
/// cannot afford a database round-trip, so it uses this lighter
/// in-process check. The two are complementary: the inline check
/// catches rapid client retries; the durable check (run by the
/// async ingestor) catches retries that span process restarts.
/// </para>
/// <para>
/// The store is intentionally <c>internal</c> -- the brief pins the
/// modal fast-path layer as a private transport-layer concern. A
/// host that needs a different store (e.g., Redis for multi-replica
/// deployments) registers its own
/// <see cref="ISlackModalFastPathHandler"/> that closes over an
/// alternative implementation.
/// </para>
/// <para>
/// <strong>Eviction.</strong> Every Slack <c>trigger_id</c> is
/// unique, so without an eager sweep the dictionary would accumulate
/// one entry per slash-command invocation for the lifetime of the
/// process. A background scavenger sweeps expired entries at
/// <see cref="DefaultScavengeInterval"/> cadence using
/// <see cref="TimeProvider.CreateTimer"/> so test hosts that supply
/// a fake clock can drive the schedule deterministically. The
/// store implements <see cref="IDisposable"/> so the singleton DI
/// registration disposes the timer on container shutdown; tests
/// that instantiate the store directly may either dispose it or
/// rely on the GC -- the timer holds the store via a
/// <see cref="WeakReference{T}"/> so an undisposed instance does
/// not pin itself in memory.
/// </para>
/// </remarks>
internal sealed class SlackInProcessIdempotencyStore : ISlackFastPathIdempotencyStore, IDisposable
{
    /// <summary>
    /// Default lifetime for an acquired idempotency token. Matches
    /// Slack's published <c>trigger_id</c> validity window (~3 s of
    /// freshness + 10 s of Slack-side retry tolerance is plenty;
    /// 15 minutes catches every duplicate the brief cares about per
    /// architecture.md §3.4).
    /// </summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Default cadence at which the background scavenger sweeps
    /// expired entries. One minute is short enough to keep the
    /// dictionary's residency proportional to the last minute of
    /// traffic (worst case) and long enough that the sweep cost is
    /// negligible (one dictionary walk per minute).
    /// </summary>
    public static readonly TimeSpan DefaultScavengeInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly ITimer? scavengeTimer;
    private int disposed;

    public SlackInProcessIdempotencyStore(TimeProvider? timeProvider = null)
        : this(timeProvider, DefaultScavengeInterval)
    {
    }

    /// <summary>
    /// Test-friendly constructor. Pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> or
    /// <see cref="TimeSpan.Zero"/> to suppress the background
    /// scavenger entirely (so a fixture can drive
    /// <see cref="Scavenge"/> manually and observe deterministic
    /// state).
    /// </summary>
    internal SlackInProcessIdempotencyStore(TimeProvider? timeProvider, TimeSpan scavengeInterval)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;

        if (scavengeInterval > TimeSpan.Zero && scavengeInterval != Timeout.InfiniteTimeSpan)
        {
            // The timer state is a WeakReference back to us, NOT a
            // closure over `this`. That breaks the otherwise-
            // unbreakable GC root chain
            //   TimerQueue -> Timer -> callback -> store
            // so an undisposed instance can still be collected by the
            // GC, at which point the next tick observes a null target
            // and short-circuits. Hosts that want prompt teardown of
            // the timer object itself call Dispose() -- singleton DI
            // registrations do this automatically on container
            // disposal.
            WeakReference<SlackInProcessIdempotencyStore> weakSelf = new(this);
            this.scavengeTimer = this.timeProvider.CreateTimer(
                ScavengeTick,
                weakSelf,
                scavengeInterval,
                scavengeInterval);
        }
    }

    /// <summary>
    /// Attempts to acquire the idempotency key. Returns
    /// <see langword="true"/> when the key is new (or its previous
    /// entry has expired); <see langword="false"/> when a fresh
    /// entry already exists.
    /// </summary>
    /// <param name="key">Idempotency key (typically
    /// <c>cmd:{team}:{user}:{cmd}:{trigger_id}</c>).</param>
    /// <param name="lifetime">Lifetime of the acquired entry. When
    /// <see langword="null"/>, <see cref="DefaultLifetime"/> is
    /// used.</param>
    /// <param name="ct">Cancellation token (currently unused; the
    /// signature mirrors <c>SlackIdempotencyGuard.TryAcquireAsync</c>
    /// so Stage 4.3 can drop in without changing call sites).</param>
    public ValueTask<bool> TryAcquireAsync(
        string key,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        TimeSpan ttl = lifetime ?? DefaultLifetime;
        DateTimeOffset expiresAt = now.Add(ttl);

        bool acquired = false;
        this.entries.AddOrUpdate(
            key,
            _ =>
            {
                acquired = true;
                return expiresAt;
            },
            (_, existing) =>
            {
                if (existing <= now)
                {
                    // Existing entry has already expired -- treat as
                    // a fresh acquire so a retry that arrived after
                    // the TTL window can still proceed.
                    acquired = true;
                    return expiresAt;
                }

                acquired = false;
                return existing;
            });

        return new ValueTask<bool>(acquired);
    }

    /// <summary>
    /// <see cref="ISlackFastPathIdempotencyStore"/> shape that wraps
    /// the boolean <see cref="TryAcquireAsync(string, TimeSpan?, CancellationToken)"/>
    /// so the in-process store can be registered directly against the
    /// interface (for tests / dev hosts that do not wire the
    /// EF-backed L2 backend).
    /// </summary>
    public async ValueTask<SlackFastPathIdempotencyResult> TryAcquireAsync(
        string key,
        SlackInboundEnvelope envelope,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        bool acquired = await this.TryAcquireAsync(key, lifetime, ct).ConfigureAwait(false);
        return acquired
            ? SlackFastPathIdempotencyResult.Acquired()
            : SlackFastPathIdempotencyResult.Duplicate(
                $"in-process L1 cache reports key '{key}' is already held.");
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(string key, CancellationToken ct = default)
    {
        this.Release(key);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// In-process stores have no durable "processing_status" column to
    /// flip -- the live dictionary entry IS the dedup signal, and the
    /// TTL handles cleanup. Implemented as a no-op so the composite
    /// store can safely delegate to L1 + L2 without special-casing.
    /// </remarks>
    public ValueTask MarkCompletedAsync(string key, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Explicitly forgets an entry. Used by failing fast-path runs
    /// (views.open returned an error) so the user can retry without
    /// being blocked by the previous attempt's idempotency
    /// reservation.
    /// </summary>
    public void Release(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        this.entries.TryRemove(key, out _);
    }

    /// <summary>
    /// Disposes the background scavenger timer. Safe to call
    /// multiple times. The dictionary itself is left intact (and
    /// eligible for GC with the rest of the store) -- no managed
    /// resources beyond the timer need explicit release.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        this.scavengeTimer?.Dispose();
    }

    /// <summary>
    /// Returns the current number of live (non-expired) entries.
    /// Exposed for tests; the production code path never calls it.
    /// </summary>
    internal int LiveCount
    {
        get
        {
            DateTimeOffset now = this.timeProvider.GetUtcNow();
            int count = 0;
            foreach (var kvp in this.entries)
            {
                if (kvp.Value > now)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Eagerly removes all entries whose TTL has elapsed. Invoked on
    /// the background scavenger timer and exposed as
    /// <c>internal</c> so tests can drive the sweep deterministically
    /// without waiting for the timer cadence.
    /// </summary>
    /// <returns>Number of entries actually removed.</returns>
    internal int Scavenge()
    {
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        int removed = 0;
        foreach (var kvp in this.entries)
        {
            if (kvp.Value <= now)
            {
                // ConcurrentDictionary.TryRemove(KeyValuePair) is
                // CAS-bounded: it only removes the entry when the
                // current value still equals the snapshot we just
                // observed, so we never accidentally evict an entry
                // that was just refreshed by a concurrent
                // TryAcquireAsync.
                if (this.entries.TryRemove(kvp))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    private static void ScavengeTick(object? state)
    {
        WeakReference<SlackInProcessIdempotencyStore> weakSelf =
            (WeakReference<SlackInProcessIdempotencyStore>)state!;
        if (!weakSelf.TryGetTarget(out SlackInProcessIdempotencyStore? self))
        {
            // The store has been collected -- nothing to do. The
            // timer object itself will be reclaimed when the GC walks
            // the TimerQueue next; until then this no-op tick is the
            // only cost.
            return;
        }

        if (Volatile.Read(ref self.disposed) != 0)
        {
            return;
        }

        try
        {
            self.Scavenge();
        }
        catch
        {
            // The scavenger MUST NOT surface exceptions onto the
            // timer thread -- unhandled exceptions from a
            // System.Threading.Timer callback crash the host. Swallow
            // defensively; the next tick will retry and lazy eviction
            // inside TryAcquireAsync continues to handle any
            // individual expired entry that is touched again.
        }
    }
}
