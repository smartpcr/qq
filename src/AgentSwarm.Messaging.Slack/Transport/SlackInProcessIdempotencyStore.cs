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
/// touch.
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
/// </remarks>
internal sealed class SlackInProcessIdempotencyStore : ISlackFastPathIdempotencyStore
{
    /// <summary>
    /// Default lifetime for an acquired idempotency token. Matches
    /// Slack's published <c>trigger_id</c> validity window (~3 s of
    /// freshness + 10 s of Slack-side retry tolerance is plenty;
    /// 15 minutes catches every duplicate the brief cares about per
    /// architecture.md §3.4).
    /// </summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, DateTimeOffset> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;

    public SlackInProcessIdempotencyStore(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
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
}
