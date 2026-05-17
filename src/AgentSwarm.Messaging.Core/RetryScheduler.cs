namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Pure exponential-backoff calculator used by <see cref="OutboxRetryEngine"/> to compute
/// the next-retry delay for a transiently failed entry. Lives in its own static class so
/// unit tests can pin the policy without instantiating the engine.
/// </summary>
public static class RetryScheduler
{
    /// <summary>
    /// Compute the delay for the supplied attempt number. The formula is
    /// <c>base * 2^(attempt - 1) ± jitter</c>, capped at <c>max</c>.
    /// </summary>
    /// <param name="attempt">1-based attempt counter (1 = first retry, 2 = second, …).</param>
    /// <param name="options">Configured retry policy.</param>
    /// <param name="random">Optional RNG for jitter (defaults to <see cref="Random.Shared"/>).</param>
    /// <returns>The delay to wait before the next attempt.</returns>
    public static TimeSpan ComputeDelay(int attempt, OutboxOptions options, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt must be >= 1.");
        }

        var rng = random ?? Random.Shared;

        var multiplier = Math.Pow(2, attempt - 1);
        var rawSeconds = options.BaseBackoffSeconds * multiplier;
        var jitterRange = rawSeconds * options.JitterRatio;
        var jitter = (rng.NextDouble() * 2.0 - 1.0) * jitterRange;
        var jittered = rawSeconds + jitter;
        var clamped = Math.Min(Math.Max(jittered, 0.0), options.MaxBackoffSeconds);
        return TimeSpan.FromSeconds(clamped);
    }

    /// <summary>
    /// Compute the next retry timestamp, honouring a server-supplied
    /// <paramref name="retryAfter"/> when it exceeds the computed exponential delay.
    /// </summary>
    public static DateTimeOffset NextRetryAt(int attempt, OutboxOptions options, TimeSpan? retryAfter, TimeProvider clock, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var computed = ComputeDelay(attempt, options, random);
        if (retryAfter is { } ra && ra > computed)
        {
            computed = ra;
        }

        return clock.GetUtcNow().Add(computed);
    }
}
