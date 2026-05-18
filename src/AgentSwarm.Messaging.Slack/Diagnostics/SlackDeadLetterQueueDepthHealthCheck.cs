// -----------------------------------------------------------------------
// <copyright file="SlackDeadLetterQueueDepthHealthCheck.cs" company="Microsoft Corp.">
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
/// </remarks>
internal sealed class SlackDeadLetterQueueDepthHealthCheck : IHealthCheck
{
    /// <summary>
    /// Health-check registration name (matches the
    /// <c>AddSlackHealthChecks</c> extension constant).
    /// </summary>
    public const string CheckName = "slack-dead-letter-queue-depth";

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
                // health output via a single InspectAsync call.
                IReadOnlyList<SlackDeadLetterEntry> snapshot =
                    await this.queue.InspectAsync(cancellationToken).ConfigureAwait(false);
                depth = snapshot.Count;
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
}
