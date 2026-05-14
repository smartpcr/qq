using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// In-memory <see cref="IActivityIdStore"/> implementation backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> with a background eviction timer. The
/// default TTL is sourced from <c>TeamsMessagingOptions.DeduplicationTtlMinutes</c>.
/// Sufficient for single-instance deployments; multi-instance deployments should swap in a
/// Redis-backed implementation.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton so the dictionary survives across requests. Implements
/// <see cref="IDisposable"/> to stop the eviction timer when the host shuts down.
/// </para>
/// </remarks>
public sealed class InMemoryActivityIdStore : IActivityIdStore, IDisposable
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly Timer _evictionTimer;
    private readonly ILogger<InMemoryActivityIdStore>? _logger;
    private bool _disposed;

    /// <summary>
    /// Initialize a new <see cref="InMemoryActivityIdStore"/>.
    /// </summary>
    /// <param name="ttlMinutes">Number of minutes a marked ID is retained before eviction.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public InMemoryActivityIdStore(int ttlMinutes, ILogger<InMemoryActivityIdStore>? logger = null)
    {
        if (ttlMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttlMinutes),
                ttlMinutes,
                "TTL must be a positive number of minutes.");
        }

        _ttl = TimeSpan.FromMinutes(ttlMinutes);
        _logger = logger;

        // Sweep every (TTL / 4) so an entry exits within ~25% past its expiry.
        var sweepInterval = TimeSpan.FromMinutes(Math.Max(1, ttlMinutes / 4.0));
        _evictionTimer = new Timer(_ => Evict(), state: null, sweepInterval, sweepInterval);
    }

    /// <inheritdoc />
    public Task<bool> IsSeenOrMarkAsync(string activityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(activityId))
        {
            throw new ArgumentException("Activity ID must be non-empty.", nameof(activityId));
        }

        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var added = _seen.TryAdd(activityId, now);
        if (!added)
        {
            // Refresh the timestamp so the duplicate-suppression window resets on each hit
            // — the contract is that the same ID must be suppressed for at least the TTL
            // window from the most recent observation.
            _seen[activityId] = now;
        }

        return Task.FromResult(!added);
    }

    /// <summary>
    /// Number of currently-tracked activity IDs. Exposed for test assertions.
    /// </summary>
    public int Count => _seen.Count;

    /// <summary>
    /// Force an eviction sweep. Exposed for test assertions; production callers rely on the
    /// background timer.
    /// </summary>
    public void Evict()
    {
        if (_disposed)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - _ttl;
        var removed = 0;

        foreach (var pair in _seen)
        {
            if (pair.Value < cutoff && _seen.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger?.LogDebug("InMemoryActivityIdStore evicted {Count} expired activity IDs.", removed);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _evictionTimer.Dispose();
    }
}
