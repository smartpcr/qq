namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Strongly typed configuration for <see cref="OutboxRetryEngine"/> per
/// <c>architecture.md</c> §9 / §10.1 and <c>implementation-plan.md</c> §6.1. Defaults align
/// with the canonical 500 ms polling cadence, 5 total delivery attempts (1 initial + 4
/// retries), 2-second base exponential-backoff, 60-second cap, and 50 msg/sec token-bucket
/// rate limit called out in <c>architecture.md</c> §10.1 and <c>implementation-plan.md</c>
/// §6.1.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// Cadence (in milliseconds) at which <see cref="OutboxRetryEngine"/> polls the
    /// outbox for pending entries. Default 500 (per <c>architecture.md</c> §9 P95
    /// budget). The poll cadence drives the lower bound on delivery latency once an
    /// entry is enqueued.
    /// </summary>
    public int PollingIntervalMs { get; set; } = 500;

    /// <summary>
    /// Maximum entries fetched per <see cref="IMessageOutbox.DequeueAsync"/> call.
    /// Default 20. Larger batches reduce database round-trips at the cost of per-batch
    /// claim contention; the engine dispatches the batch in parallel limited by
    /// <see cref="MaxDegreeOfParallelism"/>.
    /// </summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// Maximum number of dispatcher invocations executed concurrently within a single
    /// polled batch. Default 8. Combined with <see cref="BatchSize"/> this bounds the
    /// burst rate the engine pushes at the rate limiter — the rate limiter then
    /// throttles to <see cref="RateLimitPerSecond"/>.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 8;

    /// <summary>
    /// Total number of delivery attempts (initial + retries). Default 5 per
    /// <c>tech-spec.md</c> §4.4. After the final transient failure the engine
    /// dead-letters the entry.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Base delay (in seconds) for the exponential-backoff retry schedule. Default 2.
    /// Successive retries use <c>baseDelay * 2^(attempt-1)</c> with ±25% jitter, capped
    /// at <see cref="MaxBackoffSeconds"/>.
    /// </summary>
    public double BaseBackoffSeconds { get; set; } = 2.0;

    /// <summary>
    /// Hard cap on the computed backoff delay. Default 60 seconds per
    /// <c>tech-spec.md</c> §4.4.
    /// </summary>
    public double MaxBackoffSeconds { get; set; } = 60.0;

    /// <summary>
    /// Jitter applied to the computed exponential-backoff value. The actual delay is
    /// uniformly distributed in <c>[baseDelay * (1 - jitter), baseDelay * (1 + jitter)]</c>.
    /// Default 0.25 (±25%).
    /// </summary>
    public double JitterRatio { get; set; } = 0.25;

    /// <summary>
    /// Token-bucket steady-state rate (messages per second) applied to outbound
    /// deliveries. Default 50 per <c>implementation-plan.md</c> §6.1 ("token-bucket
    /// rate limiter … 50 msgs/sec per bot"). The limiter prevents the engine from
    /// burning through the Bot Connector's quota before retries kick in.
    /// </summary>
    public int RateLimitPerSecond { get; set; } = 50;

    /// <summary>
    /// Token-bucket capacity. Default 50 (matches steady-state rate). Larger bucket
    /// allows brief bursts above <see cref="RateLimitPerSecond"/>.
    /// </summary>
    public int RateLimitBurst { get; set; } = 50;

    /// <summary>
    /// Maximum time an entry may remain in <see cref="OutboxEntryStatuses.Processing"/>
    /// before another worker is allowed to reclaim its lease. Default 5 minutes —
    /// substantially larger than a worst-case Bot Connector send (typically 200–800 ms
    /// per <c>architecture.md</c> §9) so a healthy in-flight delivery never has its
    /// lease stolen, but small enough that a crashed worker's claim is recovered well
    /// before the operator notices.
    /// </summary>
    public TimeSpan ProcessingLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Histogram / counter / gauge meter name under which the engine publishes the
    /// canonical <c>teams.card.delivery.duration_ms</c>,
    /// <c>teams.outbox.pending_count</c>, and related signals listed in
    /// <c>architecture.md</c> §8.1. Default is the same value used in the architecture
    /// document so dashboards and alerts wire up out of the box.
    /// </summary>
    public string MeterName { get; set; } = "AgentSwarm.Messaging.Outbox";
}
