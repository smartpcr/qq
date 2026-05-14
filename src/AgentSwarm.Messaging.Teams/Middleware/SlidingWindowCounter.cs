namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Lock-free sliding-window counter used by <see cref="RateLimitMiddleware"/> to count
/// inbound activities per tenant over a 60-second window. Threads compete via
/// <see cref="System.Threading.Interlocked"/>; readers see a consistent snapshot.
/// </summary>
internal sealed class SlidingWindowCounter
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _hits = new();
    private readonly TimeSpan _window;

    public SlidingWindowCounter(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    /// <summary>Record a hit if the counter is currently below <paramref name="limit"/>.
    /// Returns <see langword="true"/> when the hit fits within the limit;
    /// <see langword="false"/> when the request must be throttled.</summary>
    public bool TryRecord(int limit, DateTimeOffset now)
    {
        lock (_gate)
        {
            var cutoff = now - _window;
            while (_hits.Count > 0 && _hits.Peek() < cutoff)
            {
                _hits.Dequeue();
            }

            if (_hits.Count >= limit)
            {
                return false;
            }

            _hits.Enqueue(now);
            return true;
        }
    }

    /// <summary>Compute the seconds the caller should wait before retrying when the limit
    /// was exceeded. Always returns at least 1 second so <c>Retry-After</c> is never zero.</summary>
    public int ComputeRetryAfterSeconds(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_hits.Count == 0) return 1;
            var oldest = _hits.Peek();
            var retryAt = oldest + _window;
            var delta = (retryAt - now).TotalSeconds;
            return delta <= 0 ? 1 : (int)Math.Ceiling(delta);
        }
    }
}
