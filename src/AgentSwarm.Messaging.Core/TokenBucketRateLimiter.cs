namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Lock-free token-bucket rate limiter implementing the canonical 50 msg/sec ceiling per
/// <c>implementation-plan.md</c> §6.1 step "token-bucket rate limiter in the outbound
/// pipeline to proactively avoid Bot Framework rate limits". The bucket refills at
/// <c>RateLimitPerSecond</c> tokens per second and caps at <c>RateLimitBurst</c>.
/// </summary>
/// <remarks>
/// <para>
/// Acquisition is asynchronous and cooperative: callers <c>await</c>
/// <see cref="AcquireAsync"/> which yields a small <c>Task.Delay</c> until at
/// least one token is available, then atomically decrements the bucket. The
/// <see cref="TimeProvider"/> is injected so unit tests advance a fake clock without
/// wall-clock flakiness.
/// </para>
/// <para>
/// The implementation favours simplicity over fairness: there is no waiter queue, so
/// thundering-herd contention may yield small bursts above the steady-state rate when
/// many callers wake from the same delay window. That is acceptable for the outbox
/// engine where the burst is bounded by <see cref="OutboxOptions.MaxDegreeOfParallelism"/>
/// and the Bot Connector itself enforces the hard 429 ceiling.
/// </para>
/// </remarks>
public sealed class TokenBucketRateLimiter
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private double _tokens;
    private DateTimeOffset _lastRefill;

    /// <summary>Construct the limiter with the supplied options and clock.</summary>
    /// <param name="options">Configured outbox options.</param>
    /// <param name="timeProvider">Optional injected clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public TokenBucketRateLimiter(OutboxOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RateLimitPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RateLimitPerSecond, "RateLimitPerSecond must be > 0.");
        }

        if (options.RateLimitBurst <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RateLimitBurst, "RateLimitBurst must be > 0.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _ratePerSecond = options.RateLimitPerSecond;
        _capacity = options.RateLimitBurst;
        _tokens = _capacity;
        _lastRefill = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Wait until at least one token is available, then atomically consume one. Returns
    /// immediately when tokens are available; otherwise awaits a small delay before
    /// retrying. Respects <paramref name="ct"/>.
    /// </summary>
    public async Task AcquireAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            lock (_sync)
            {
                RefillUnlocked();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }

                var deficit = 1.0 - _tokens;
                var seconds = deficit / _ratePerSecond;
                wait = TimeSpan.FromSeconds(Math.Max(seconds, 0.001));
            }

            await Task.Delay(wait, _timeProvider, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    private void RefillUnlocked()
    {
        var now = _timeProvider.GetUtcNow();
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + (elapsed * _ratePerSecond));
        _lastRefill = now;
    }
}
