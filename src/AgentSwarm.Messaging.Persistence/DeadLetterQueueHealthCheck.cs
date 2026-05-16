// -----------------------------------------------------------------------
// <copyright file="DeadLetterQueueHealthCheck.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 4.2 — composite-friendly <see cref="IHealthCheck"/> that
/// reports the host <see cref="HealthStatus.Unhealthy"/> when the
/// dead-letter queue depth exceeds
/// <see cref="DeadLetterQueueOptions.UnhealthyThreshold"/>. Plugged
/// into the worker's <c>AddHealthChecks()</c> registration so the
/// existing <c>/healthz</c> liveness probe upgrades from a static
/// "200 OK" to a live "is the operator drowning in dead-letters?"
/// signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Status mapping.</b>
/// <list type="bullet">
///   <item><description>
///   <c>count &lt;= threshold</c> →
///   <see cref="HealthStatus.Healthy"/> — normal operations,
///   dead-letter backlog within the operator's tolerance.
///   </description></item>
///   <item><description>
///   <c>count &gt; threshold</c> →
///   <see cref="HealthStatus.Unhealthy"/> — Stage 4.2 test scenario
///   "Given 10 messages in the dead-letter queue and threshold is 5,
///   When health check is queried, Then status is Unhealthy."
///   </description></item>
///   <item><description>
///   Any exception thrown by
///   <see cref="IDeadLetterQueue.CountAsync"/> (database
///   unavailable, transient I/O) →
///   <see cref="HealthStatus.Unhealthy"/> with the exception
///   recorded so the operator runbook surfaces the actual failure
///   rather than a confusing healthy result.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>No backpressure.</b> The check is a signal-only sensor — it
/// does NOT block the dead-letter queue from accepting more rows.
/// Stage 4.2 deliberately keeps "queue is large" separate from
/// "queue is rejecting writes" so the operator audit screen retains
/// full visibility into the burst.
/// </para>
/// </remarks>
public sealed class DeadLetterQueueHealthCheck : IHealthCheck
{
    /// <summary>
    /// Canonical name to register this check under via
    /// <see cref="HealthChecksBuilderAddCheckExtensions.AddCheck{T}(IHealthChecksBuilder, string, HealthStatus?, IEnumerable{string}?)"/>.
    /// </summary>
    public const string Name = "outbound_dead_letter_queue_depth";

    private readonly IDeadLetterQueue _deadLetterQueue;
    private readonly DeadLetterQueueOptions _options;

    public DeadLetterQueueHealthCheck(
        IDeadLetterQueue deadLetterQueue,
        IOptions<DeadLetterQueueOptions> options)
    {
        _deadLetterQueue = deadLetterQueue ?? throw new ArgumentNullException(nameof(deadLetterQueue));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value
            ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        int count;
        try
        {
            count = await _deadLetterQueue.CountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: "Failed to read dead-letter queue depth — backing store is unreachable or throwing. The health check assumes the worst (Unhealthy) so the operator runbook surfaces the actual failure rather than silently passing on a count of zero.",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["threshold"] = _options.UnhealthyThreshold,
                });
        }

        var threshold = _options.UnhealthyThreshold;
        var data = new Dictionary<string, object>
        {
            ["count"] = count,
            ["threshold"] = threshold,
        };

        if (count > threshold)
        {
            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: $"Dead-letter queue depth {count} exceeds configured threshold {threshold}. Operator action: triage the {Name} backlog.",
                data: data);
        }

        return new HealthCheckResult(
            HealthStatus.Healthy,
            description: $"Dead-letter queue depth {count} is within threshold {threshold}.",
            data: data);
    }
}
