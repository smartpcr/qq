// -----------------------------------------------------------------------
// <copyright file="SlackDeadLetterQueueDepthHealthCheck.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// ASP.NET Core <see cref="IHealthCheck"/> that reports
/// <c>Unhealthy</c> when the configured dead-letter queue's depth
/// exceeds
/// <see cref="SlackHealthCheckOptions.DeadLetterUnhealthyThreshold"/>
/// (default <c>100</c> per Stage 7.3 step 3 of the implementation
/// plan).
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 3: "Register health check for DLQ depth: report
/// <c>Unhealthy</c> if DLQ depth exceeds a configurable threshold
/// (default 100)." Brief test scenario: "Given DLQ depth at 150
/// (threshold 100), When the health check runs, Then it reports
/// <c>Unhealthy</c> with a descriptive message."
/// </para>
/// <para>
/// Depth is sampled through the optional
/// <see cref="ISlackDeadLetterQueueDepthProbe"/> exposed by the
/// registered <see cref="ISlackDeadLetterQueue"/> concrete type (the
/// in-memory queue and the durable file-system queue both implement
/// the probe). When the registered queue does NOT implement the
/// probe the check falls back to
/// <see cref="ISlackDeadLetterQueue.InspectAsync"/>, but the result
/// is CACHED for <see cref="FallbackInspectCacheTtl"/> (30 seconds)
/// keyed by the queue instance. Kubernetes readiness probes typically
/// fire every 5-10 seconds, so an uncached fallback would issue a
/// full JSONL scan on every probe -- exactly when a DLQ backlog
/// (the scenario this check exists to detect) makes that scan most
/// expensive. The TTL serves ~3-6 consecutive probes from a single
/// durable read while keeping the staleness window short enough that
/// alerting still fires inside an operator-acceptable interval.
/// </para>
/// <para>
/// In addition to the cache, the check logs a one-time
/// <see cref="LogLevel.Warning"/> per queue instance when the
/// registered backend does not implement
/// <see cref="ISlackDeadLetterQueueDepthProbe"/> so operators know
/// the probe is running in degraded (snapshot-fallback) mode and can
/// plug in a queue that exposes the cheap probe to recover the
/// hot-path performance. The marker is held in a static
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> so it does not
/// keep the queue alive past its own DI lifetime.
/// </para>
/// </remarks>
internal sealed class SlackDeadLetterQueueDepthHealthCheck : IHealthCheck
{
    /// <summary>
    /// Health-check registration name (matches the
    /// <c>AddSlackHealthChecks</c> extension constant).
    /// </summary>
    public const string CheckName = "slack-dead-letter-queue-depth";

    /// <summary>
    /// TTL applied to the cached
    /// <see cref="ISlackDeadLetterQueue.InspectAsync"/> result when
    /// the registered queue does not expose
    /// <see cref="ISlackDeadLetterQueueDepthProbe"/>. Exposed as
    /// <c>internal</c> so unit tests can pin the boundary without
    /// duplicating the literal.
    /// </summary>
    internal static readonly TimeSpan FallbackInspectCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-queue fallback cache, keyed by queue instance. Static so
    /// the cache state survives the health-check object's own DI
    /// lifetime (the
    /// <see cref="Microsoft.Extensions.DependencyInjection.HealthChecksBuilderAddCheckExtensions.AddCheck"/>
    /// registration resolves a fresh check instance per probe when
    /// the health check is registered as transient, which is the
    /// default). <see cref="ConditionalWeakTable{TKey,TValue}"/>
    /// releases the cache entry automatically once the queue itself
    /// is collected, so a host that swaps queue backends does not
    /// leak the cache.
    /// </summary>
    private static readonly ConditionalWeakTable<ISlackDeadLetterQueue, FallbackCache> fallbackCaches = new();

    /// <summary>
    /// Per-queue "degraded mode warning already emitted" marker. The
    /// presence of an entry indicates the warning has been logged at
    /// least once for that queue instance in this process; absence
    /// means the next constructor call SHOULD log the warning and
    /// then claim the slot. <see cref="ConditionalWeakTable{TKey,TValue}.TryAdd(TKey,TValue)"/>
    /// is thread-safe and races deterministically: exactly one
    /// concurrent constructor will see <see langword="true"/>.
    /// </summary>
    private static readonly ConditionalWeakTable<ISlackDeadLetterQueue, object> degradedModeWarningEmitted = new();

    private static readonly object DegradedModeWarningSentinel = new();

    private readonly ISlackDeadLetterQueue queue;
    private readonly IOptionsMonitor<SlackHealthCheckOptions> options;
    private readonly ILogger<SlackDeadLetterQueueDepthHealthCheck> logger;
    private readonly TimeProvider timeProvider;
    private readonly bool usesFallbackInspect;

    public SlackDeadLetterQueueDepthHealthCheck(
        ISlackDeadLetterQueue queue,
        IOptionsMonitor<SlackHealthCheckOptions> options,
        ILogger<SlackDeadLetterQueueDepthHealthCheck> logger,
        TimeProvider? timeProvider = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.usesFallbackInspect = this.queue is not ISlackDeadLetterQueueDepthProbe;

        if (this.usesFallbackInspect)
        {
            this.EmitDegradedModeWarningOnce();
        }
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        SlackHealthCheckOptions opts = this.options.CurrentValue;
        int threshold = opts.EffectiveDeadLetterUnhealthyThreshold;

        int depth;
        try
        {
            if (this.queue is ISlackDeadLetterQueueDepthProbe probe)
            {
                depth = probe.GetCurrentDepth();
            }
            else
            {
                // Fall-back: a custom queue backend that does not
                // implement the cheap probe still produces correct
                // health output via InspectAsync, but the result is
                // cached for FallbackInspectCacheTtl so a backlogged
                // DLQ does not get re-scanned (potentially a full
                // JSONL file read) on every Kubernetes readiness
                // probe.
                depth = await this.GetDepthViaCachedInspectAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack DLQ depth probe threw; treating as Unhealthy so the operator notices the inspection failure.");
            return HealthCheckResult.Unhealthy(
                description: $"DLQ depth probe threw {ex.GetType().Name}: {ex.Message}",
                exception: ex,
                data: new Dictionary<string, object?>
                {
                    ["threshold"] = threshold,
                    ["depth"] = null,
                    ["queue_type"] = this.queue.GetType().FullName,
                }!);
        }

        if (depth < 0)
        {
            depth = 0;
        }

        Dictionary<string, object> data = new()
        {
            ["threshold"] = threshold,
            ["depth"] = depth,
            ["queue_type"] = this.queue.GetType().FullName ?? this.queue.GetType().Name,
        };

        if (depth > threshold)
        {
            return HealthCheckResult.Unhealthy(
                description: $"Slack DLQ depth {depth} exceeds Unhealthy threshold {threshold}; operator triage required.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            description: $"Slack DLQ depth {depth} is within threshold {threshold}.",
            data: data);
    }

    /// <summary>
    /// Internal test seam: returns the cached depth (and the
    /// timestamp it was captured at) for the queue instance bound
    /// to this check, or <see langword="null"/> when no cache entry
    /// exists yet. Used by the unit tests to assert the cache
    /// behaviour without reaching into static state directly.
    /// </summary>
    internal (int Depth, DateTimeOffset CapturedAt)? PeekFallbackCache()
    {
        if (!fallbackCaches.TryGetValue(this.queue, out FallbackCache? cache) || cache is null)
        {
            return null;
        }

        lock (cache.SyncRoot)
        {
            return cache.Timestamp is { } captured
                ? (cache.Depth, captured)
                : null;
        }
    }

    private async Task<int> GetDepthViaCachedInspectAsync(CancellationToken ct)
    {
        FallbackCache cache = fallbackCaches.GetValue(this.queue, static _ => new FallbackCache());

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        if (cache.TryReadFresh(now, FallbackInspectCacheTtl, out int cachedDepth))
        {
            return cachedDepth;
        }

        // Serialise refreshes so a burst of concurrent probes does
        // not stampede the underlying InspectAsync call (which, for
        // the file-system backend, parses every JSONL line).
        await cache.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate: another caller may
            // have refreshed while we were waiting.
            now = this.timeProvider.GetUtcNow();
            if (cache.TryReadFresh(now, FallbackInspectCacheTtl, out cachedDepth))
            {
                return cachedDepth;
            }

            IReadOnlyList<SlackDeadLetterEntry> snapshot =
                await this.queue.InspectAsync(ct).ConfigureAwait(false);
            int depth = snapshot?.Count ?? 0;
            cache.Write(depth, this.timeProvider.GetUtcNow());
            return depth;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    private void EmitDegradedModeWarningOnce()
    {
        if (!degradedModeWarningEmitted.TryAdd(this.queue, DegradedModeWarningSentinel))
        {
            // Another constructor (or an earlier one in this process)
            // already claimed the slot and emitted the warning -- do
            // not spam the log on every transient re-activation.
            return;
        }

        this.logger.LogWarning(
            "Slack DLQ depth health check '{CheckName}' is running in DEGRADED mode: the registered ISlackDeadLetterQueue '{QueueType}' does not implement ISlackDeadLetterQueueDepthProbe, so depth sampling falls back to ISlackDeadLetterQueue.InspectAsync. The result is cached for {CacheTtlSeconds}s between probes to avoid amplifying I/O load (a Kubernetes readiness probe firing every 5-10 seconds would otherwise issue one full snapshot read per probe). Implement ISlackDeadLetterQueueDepthProbe on the queue backend to remove this warning and restore the cheap probe path.",
            CheckName,
            this.queue.GetType().FullName ?? this.queue.GetType().Name,
            (int)FallbackInspectCacheTtl.TotalSeconds);
    }

    /// <summary>
    /// Mutable per-queue cache entry guarded by
    /// <see cref="SyncRoot"/> for field reads and by
    /// <see cref="Gate"/> for serialising the (potentially expensive)
    /// <see cref="ISlackDeadLetterQueue.InspectAsync"/> refresh.
    /// </summary>
    private sealed class FallbackCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public object SyncRoot { get; } = new();

        public DateTimeOffset? Timestamp;

        public int Depth;

        public bool TryReadFresh(DateTimeOffset now, TimeSpan ttl, out int depth)
        {
            lock (this.SyncRoot)
            {
                if (this.Timestamp is { } captured && (now - captured) < ttl)
                {
                    depth = this.Depth;
                    return true;
                }
            }

            depth = 0;
            return false;
        }

        public void Write(int depth, DateTimeOffset capturedAt)
        {
            lock (this.SyncRoot)
            {
                this.Depth = depth;
                this.Timestamp = capturedAt;
            }
        }
    }
}
