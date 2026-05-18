// -----------------------------------------------------------------------
// <copyright file="SlackOutboundQueueDepthHealthCheck.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// ASP.NET Core <see cref="IHealthCheck"/> that reports
/// <c>Degraded</c> when the configured outbound queue's depth exceeds
/// <see cref="SlackHealthCheckOptions.OutboundQueueDegradedThreshold"/>
/// (default <c>1000</c> per Stage 7.3 step 2 of the implementation
/// plan).
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 2: "Register health check for outbound queue depth: report
/// <c>Degraded</c> if queue depth exceeds a configurable threshold
/// (default 1000)."
/// </para>
/// <para>
/// Depth is sampled through the optional
/// <see cref="ISlackOutboundQueueDepthProbe"/> exposed by the
/// registered <see cref="ISlackOutboundQueue"/> concrete type (the
/// in-process channel queue and the durable file-system queue both
/// implement the probe). When the registered queue does NOT implement
/// the probe (e.g., a host swaps in a custom backend) the check
/// returns <c>Healthy</c> with <c>data["depth"] = null</c> so the
/// readiness signal stays positive rather than spuriously degrading.
/// </para>
/// <para>
/// If <see cref="ISlackOutboundQueueDepthProbe.GetCurrentDepth"/>
/// throws, the check returns <c>Degraded</c> (not <c>Healthy</c>) so
/// that genuine queue infrastructure failures are surfaced in the
/// structured readiness payload. <c>Degraded</c> still maps to
/// HTTP&#160;200 so the pod stays in Kubernetes rotation while the
/// failure becomes visible to operators and alerting through
/// <c>status=Degraded</c> and the <c>probe_error</c> data entry.
/// </para>
/// </remarks>
internal sealed class SlackOutboundQueueDepthHealthCheck : IHealthCheck
{
    /// <summary>
    /// Health-check registration name (matches the
    /// <c>AddSlackHealthChecks</c> extension constant).
    /// </summary>
    public const string CheckName = "slack-outbound-queue-depth";

    private readonly ISlackOutboundQueue queue;
    private readonly IOptionsMonitor<SlackHealthCheckOptions> options;
    private readonly ILogger<SlackOutboundQueueDepthHealthCheck> logger;

    public SlackOutboundQueueDepthHealthCheck(
        ISlackOutboundQueue queue,
        IOptionsMonitor<SlackHealthCheckOptions> options,
        ILogger<SlackOutboundQueueDepthHealthCheck> logger)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SlackHealthCheckOptions opts = this.options.CurrentValue;
        int threshold = opts.EffectiveOutboundDegradedThreshold;

        if (this.queue is not ISlackOutboundQueueDepthProbe probe)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                description: $"Configured outbound queue ({this.queue.GetType().Name}) does not expose ISlackOutboundQueueDepthProbe; depth not sampled (threshold={threshold}).",
                data: new Dictionary<string, object?>
                {
                    ["threshold"] = threshold,
                    ["depth"] = null,
                    ["queue_type"] = this.queue.GetType().FullName,
                }!));
        }

        int depth;
        try
        {
            depth = probe.GetCurrentDepth();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack outbound-queue depth probe threw; reporting Degraded so the readiness payload surfaces the probe failure to operators.");
            return Task.FromResult(HealthCheckResult.Degraded(
                description: $"Outbound queue depth probe threw {ex.GetType().Name}; reporting Degraded so the failure is visible in the readiness payload.",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["threshold"] = threshold,
                    ["depth"] = null!,
                    ["queue_type"] = (object?)this.queue.GetType().FullName ?? this.queue.GetType().Name,
                    ["probe_error"] = ex.Message,
                    ["probe_error_type"] = ex.GetType().FullName ?? ex.GetType().Name,
                }));
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
            return Task.FromResult(HealthCheckResult.Degraded(
                description: $"Slack outbound queue depth {depth} exceeds Degraded threshold {threshold}.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            description: $"Slack outbound queue depth {depth} is within threshold {threshold}.",
            data: data));
    }
}
