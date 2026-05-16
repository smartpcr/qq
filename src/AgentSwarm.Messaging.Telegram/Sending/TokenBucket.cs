using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentSwarm.Messaging.Telegram.Sending;

/// <summary>
/// Leaky / fractional token bucket. Refills <see cref="RefillPerSecond"/>
/// tokens per second up to <see cref="Capacity"/>; <see cref="AcquireAsync"/>
/// asynchronously awaits one token, scheduling its return based on the
/// configured <see cref="TimeProvider"/> so tests can run without real
/// wall-clock delays.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fractional accounting.</b> Tokens are stored as a <see cref="double"/>
/// so the bucket can refill smoothly between integer ticks (e.g. 20
/// tokens/min = 1 token per 3 s — at 1.5 s, the bucket has 0.5 tokens
/// available, and a request issued at 3 s sees a full token). This is
/// the textbook token-bucket implementation, kept lock-protected for
/// thread safety because the rate limiter is shared across worker
/// concurrency (architecture.md §10.4 "10 concurrent workers").
/// </para>
/// <para>
/// <b>Wait scheduling.</b> When the bucket is empty, the awaiter
/// computes the precise time until the next token is available and
/// awaits a <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// pinned to the injected provider. <see cref="TimeProvider.System"/>
/// is the production default; tests inject a fake provider so the
/// async wait completes deterministically without real delays.
/// </para>
/// <para>
/// <b>Cancellation safety.</b> A cancelled wait does NOT consume a
/// token. Cancellation is observed mid-wait and the method throws
/// <see cref="OperationCanceledException"/> before the token would have
/// been debited.
/// </para>
/// </remarks>
internal sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private double _tokens;
    private DateTimeOffset _lastRefill;

    public TokenBucket(double capacity, double refillPerSecond, TimeProvider timeProvider)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "must be positive.");
        }
        if (refillPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(refillPerSecond), refillPerSecond, "must be positive.");
        }

        Capacity = capacity;
        RefillPerSecond = refillPerSecond;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _tokens = capacity;
        _lastRefill = timeProvider.GetUtcNow();
    }

    public double Capacity { get; }

    public double RefillPerSecond { get; }

    /// <summary>
    /// Snapshot of the current token balance. For diagnostics / tests only.
    /// </summary>
    public double CurrentTokens
    {
        get
        {
            lock (_gate)
            {
                RefillUnsafe();
                return _tokens;
            }
        }
    }

    /// <summary>
    /// Acquire one token, blocking asynchronously until one becomes
    /// available. Returns immediately when the bucket has at least one
    /// token. Honours <paramref name="cancellationToken"/>: a cancelled
    /// wait throws <see cref="OperationCanceledException"/> WITHOUT
    /// consuming a token.
    /// </summary>
    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                RefillUnsafe();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }

                var deficit = 1.0 - _tokens;
                var secondsToWait = deficit / RefillPerSecond;
                wait = TimeSpan.FromSeconds(secondsToWait);
            }

            // Floor the delay at a small positive value so we always
            // yield control even when the deficit is microscopic.
            if (wait < TimeSpan.FromMilliseconds(1))
            {
                wait = TimeSpan.FromMilliseconds(1);
            }

            await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RefillUnsafe()
    {
        var now = _timeProvider.GetUtcNow();
        var elapsed = now - _lastRefill;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        _tokens = Math.Min(Capacity, _tokens + elapsed.TotalSeconds * RefillPerSecond);
        _lastRefill = now;
    }
}
