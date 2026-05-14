using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Storage;

/// <summary>
/// In-memory <see cref="IActivityIdStore"/> sufficient for single-instance deployments and
/// integration tests. Entries are evicted lazily on read once they exceed the TTL configured
/// by <see cref="TeamsMessagingOptions.DeduplicationTtlMinutes"/>; multi-instance deployments
/// should swap in a Redis-backed implementation.
/// </summary>
public sealed class InMemoryActivityIdStore : IActivityIdStore, IDisposable
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen
        = new(StringComparer.Ordinal);

    private readonly IOptionsMonitor<TeamsMessagingOptions> _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer? _evictionTimer;

    /// <summary>Initialize a new <see cref="InMemoryActivityIdStore"/>.</summary>
    public InMemoryActivityIdStore(IOptionsMonitor<TeamsMessagingOptions> options)
        : this(options, clock: null, enableBackgroundEviction: true)
    {
    }

    internal InMemoryActivityIdStore(
        IOptionsMonitor<TeamsMessagingOptions> options,
        Func<DateTimeOffset>? clock,
        bool enableBackgroundEviction)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        if (enableBackgroundEviction)
        {
            _evictionTimer = new Timer(
                _ => Evict(),
                state: null,
                dueTime: TimeSpan.FromMinutes(1),
                period: TimeSpan.FromMinutes(1));
        }
    }

    /// <inheritdoc />
    public Task<bool> IsSeenOrMarkAsync(string activityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(activityId))
        {
            throw new ArgumentException("Activity id must be non-empty.", nameof(activityId));
        }
        ct.ThrowIfCancellationRequested();

        var now = _clock();
        var ttl = TimeSpan.FromMinutes(_options.CurrentValue.DeduplicationTtlMinutes);

        // Single AddOrUpdate atomically inserts when new (caller wins) or returns existing
        // value for duplicate.
        var winner = _seen.AddOrUpdate(
            activityId,
            addValueFactory: _ => now,
            updateValueFactory: (_, existing) =>
            {
                if (now - existing > ttl)
                {
                    // Stale entry — treat as new and reset the timestamp.
                    return now;
                }
                return existing;
            });

        var seen = winner != now;
        return Task.FromResult(seen);
    }

    /// <summary>Evict entries whose age exceeds the configured TTL.</summary>
    public void Evict()
    {
        var now = _clock();
        var ttl = TimeSpan.FromMinutes(_options.CurrentValue.DeduplicationTtlMinutes);

        foreach (var kvp in _seen)
        {
            if (now - kvp.Value > ttl)
            {
                _seen.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _evictionTimer?.Dispose();
}
