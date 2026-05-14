namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Sliding-window counter used by <see cref="RateLimitMiddleware"/> to enforce per-tenant
/// activity quotas. Aligned with the Stage 2.1 implementation plan: each tenant maintains a
/// rolling 60-second window of timestamps; observations outside the window are evicted
/// before each <see cref="TryAcquire"/> call.
/// </summary>
/// <remarks>
/// <para>
/// The counter is thread-safe: <see cref="TryAcquire"/> acquires an internal lock for the
/// brief duration of the eviction + count + write sequence. The lock granularity is
/// per-counter (so per-tenant) — tenants do not contend with each other.
/// </para>
/// </remarks>
public sealed class SlidingWindowCounter
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _timestamps = new();
    private readonly TimeSpan _window;
    private readonly int _limit;

    /// <summary>
    /// Initialize a new <see cref="SlidingWindowCounter"/>.
    /// </summary>
    /// <param name="limit">Maximum number of observations per window.</param>
    /// <param name="window">The rolling window duration.</param>
    public SlidingWindowCounter(int limit, TimeSpan window)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be positive.");
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window), window, "Window must be positive.");

        _limit = limit;
        _window = window;
    }

    /// <summary>
    /// Try to acquire a slot in the current window.
    /// </summary>
    /// <param name="now">The observation timestamp (typically <see cref="DateTimeOffset.UtcNow"/>).</param>
    /// <param name="retryAfter">
    /// When the call returns <c>false</c>, the duration the caller should wait before
    /// retrying (computed from the oldest observation in the window). <see cref="TimeSpan.Zero"/>
    /// on success.
    /// </param>
    /// <returns><c>true</c> if the slot was acquired; <c>false</c> if the limit was exceeded.</returns>
    public bool TryAcquire(DateTimeOffset now, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            Evict(now);

            if (_timestamps.Count >= _limit)
            {
                var oldest = _timestamps.Peek();
                var elapsed = now - oldest;
                var remaining = _window - elapsed;
                retryAfter = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
                return false;
            }

            _timestamps.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>
    /// Number of observations currently within the rolling window. Exposed for testing.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _timestamps.Count;
            }
        }
    }

    private void Evict(DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (_timestamps.Count > 0 && _timestamps.Peek() <= cutoff)
        {
            _timestamps.Dequeue();
        }
    }
}
