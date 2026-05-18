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
/// probe the check falls back to a single
/// <see cref="ISlackDeadLetterQueue.InspectAsync"/> call -- still
/// correct, just slower for large JSONL files; backends used in
/// production all opt into the cheap probe path.
/// </para>
/// <para>
/// <b>Stage 7.3 review-r0 item 3 -- fallback hot-path mitigation.</b>
/// Kubernetes readiness probes fire every 5-10 seconds; under the
/// exact DLQ backlog this check exists to detect, a per-probe
/// <c>InspectAsync</c> against the file-system backend would parse
/// every JSONL line on every invocation and amplify I/O load at
/// precisely the worst moment. To protect against that we:
/// </para>
/// <list type="number">
///   <item><description>Cache the fallback <c>InspectAsync</c> result
///   for <see cref="FallbackInspectTtl"/> (30&#160;s) per queue
///   instance, serialise concurrent refreshes through a
///   <see cref="SemaphoreSlim"/> (thundering-herd protection), and
///   surface <c>depth_from_fallback_cache</c> /
///   <c>fallback_cache_age_ms</c> /
///   <c>fallback_cache_ttl_ms</c> in the structured health
///   payload so operators can see exactly when a probe answered from
///   the cache.</description></item>
///   <item><description>Emit a single <c>LogWarning</c> per queue
///   instance the FIRST time the fallback path is taken so operators
///   immediately know the probe is running in degraded mode and can
///   switch to a queue type that implements
///   <see cref="ISlackDeadLetterQueueDepthProbe"/>
///   (the in-memory and file-system backends both do).</description></item>
/// </list>
/// <para>
/// Cache state lives in a static
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed by queue
/// reference so (a) the cache survives across the transient
/// health-check instances ASP.NET Core's
/// <c>HealthCheckService</c> creates per probe invocation, and (b)
/// the cache entry (and its <see cref="SemaphoreSlim"/>) is
/// reclaimed automatically when the queue instance is GC'd -- the
/// queue is a DI singleton in production, so in practice the cache
/// lives for the process lifetime.
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
    /// TTL applied to the fallback <c>InspectAsync</c> result so a
    /// Kubernetes probe firing every 5-10&#160;s does not trigger a
    /// JSONL full-scan on every invocation. 30&#160;s is short enough
    /// to keep the readiness signal current (an operator triaging an
    /// alert sees a fresh sample within half a minute) and long
    /// enough to collapse hundreds of probe calls into a single
    /// inspection during a backlog.
    /// </summary>
    private static readonly TimeSpan FallbackInspectTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-queue cache + degraded-mode-warning state. Keyed by
    /// queue reference so a host that swaps the
    /// <see cref="ISlackDeadLetterQueue"/> registration (or a unit
    /// test that constructs a fresh fake per scenario) gets a fresh
    /// slot without leaking state across instances.
    /// </summary>
    private static readonly ConditionalWeakTable<ISlackDeadLetterQueue, FallbackState> FallbackStates = new();

    private readonly ISlackDeadLetterQueue queue;
    private readonly IOptionsMonitor<SlackHealthCheckOptions> options;
    private readonly ILogger<SlackDeadLetterQueueDepthHealthCheck> logger;

    public SlackDeadLetterQueueDepthHealthCheck(
        ISlackDeadLetterQueue queue,
        IOptionsMonitor<SlackHealthCheckOptions> options,
        ILogger<SlackDeadLetterQueueDepthHealthCheck> logger)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        SlackHealthCheckOptions opts = this.options.CurrentValue;
        int threshold = opts.EffectiveDeadLetterUnhealthyThreshold;

        int depth;
        bool depthFromFallbackCache = false;
        long fallbackCacheAgeMs = 0;
        bool fallbackPathTaken = false;
        try
        {
            if (this.queue is ISlackDeadLetterQueueDepthProbe probe)
            {
                depth = probe.GetCurrentDepth();
            }
            else
            {
                fallbackPathTaken = true;
                FallbackState state = FallbackStates.GetValue(this.queue, _ => new FallbackState());
                this.EmitDegradedModeWarningOnce(state);
                (depth, depthFromFallbackCache, fallbackCacheAgeMs) =
                    await this.SampleDepthThroughFallbackCacheAsync(state, cancellationToken).ConfigureAwait(false);
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

        // Surface fallback-cache diagnostics ONLY when the fallback
        // path was taken; the cheap-probe path has no caching to
        // report and adding null entries would pollute the payload.
        if (fallbackPathTaken)
        {
            data["depth_from_fallback_cache"] = depthFromFallbackCache;
            data["fallback_cache_age_ms"] = fallbackCacheAgeMs;
            data["fallback_cache_ttl_ms"] = (long)FallbackInspectTtl.TotalMilliseconds;
        }

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
    /// Logs a single <c>Warning</c> the first time the fallback path
    /// is taken for a given queue instance so operators immediately
    /// know the probe is sampling depth via the expensive
    /// <c>InspectAsync</c> call. Subsequent probe invocations against
    /// the same queue stay silent -- the warning is for awareness,
    /// not per-probe alert noise.
    /// </summary>
    private void EmitDegradedModeWarningOnce(FallbackState state)
    {
        if (Interlocked.Exchange(ref state.WarningEmitted, 1) != 0)
        {
            return;
        }

        this.logger.LogWarning(
            "Slack DLQ depth check running in DEGRADED mode: configured queue {QueueType} does not implement ISlackDeadLetterQueueDepthProbe, so the probe falls back to ISlackDeadLetterQueue.InspectAsync (full-scan; for the file-system backend this parses every JSONL line). The fallback result is cached for {FallbackTtlSeconds}s to protect the Kubernetes probe hot path, but a backend that implements the cheap probe (e.g. InMemorySlackDeadLetterQueue, FileSystemSlackDeadLetterQueue) is strongly preferred.",
            this.queue.GetType().FullName ?? this.queue.GetType().Name,
            (int)FallbackInspectTtl.TotalSeconds);
    }

    /// <summary>
    /// Returns the cached fallback depth when the cached sample is
    /// younger than <see cref="FallbackInspectTtl"/>; otherwise
    /// serialises through the per-queue
    /// <see cref="FallbackState.Semaphore"/> and refreshes the cache
    /// with a single <see cref="ISlackDeadLetterQueue.InspectAsync"/>
    /// call. Concurrent probes that arrive while a refresh is in
    /// flight wait on the semaphore and then re-use the freshly
    /// stored sample (thundering-herd protection).
    /// </summary>
    private async Task<(int Depth, bool FromCache, long AgeMs)> SampleDepthThroughFallbackCacheAsync(
        FallbackState state,
        CancellationToken cancellationToken)
    {
        long ttlMs = (long)FallbackInspectTtl.TotalMilliseconds;

        // Lock-free fast path: a still-fresh sample is returned
        // without taking the semaphore so steady-state probes do
        // zero contention.
        if (TryReadFreshSample(state, ttlMs, out int fastDepth, out long fastAgeMs))
        {
            return (fastDepth, true, fastAgeMs);
        }

        await state.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check inside the semaphore: a concurrent probe
            // may have refreshed the sample while we were waiting.
            if (TryReadFreshSample(state, ttlMs, out int dcDepth, out long dcAgeMs))
            {
                return (dcDepth, true, dcAgeMs);
            }

            IReadOnlyList<SlackDeadLetterEntry> snapshot =
                await this.queue.InspectAsync(cancellationToken).ConfigureAwait(false);
            int depth = snapshot.Count;
            if (depth < 0)
            {
                depth = 0;
            }

            Volatile.Write(ref state.CachedDepth, depth);
            Volatile.Write(ref state.SampledAtTickCount, Environment.TickCount64);
            Volatile.Write(ref state.HasSample, 1);
            return (depth, false, 0L);
        }
        finally
        {
            state.Semaphore.Release();
        }
    }

    private static bool TryReadFreshSample(FallbackState state, long ttlMs, out int depth, out long ageMs)
    {
        if (Volatile.Read(ref state.HasSample) == 0)
        {
            depth = 0;
            ageMs = 0;
            return false;
        }

        long sampledAt = Volatile.Read(ref state.SampledAtTickCount);
        long age = Environment.TickCount64 - sampledAt;
        if (age < 0 || age >= ttlMs)
        {
            depth = 0;
            ageMs = 0;
            return false;
        }

        depth = Volatile.Read(ref state.CachedDepth);
        ageMs = age;
        return true;
    }

    /// <summary>
    /// Per-queue cache and degraded-mode-warning state. Public fields
    /// (not properties) so <see cref="Interlocked"/> /
    /// <see cref="Volatile"/> can operate on them by reference.
    /// </summary>
    private sealed class FallbackState
    {
        public int WarningEmitted;
        public int HasSample;
        public int CachedDepth;
        public long SampledAtTickCount;
        public readonly SemaphoreSlim Semaphore = new(1, 1);
    }
}
