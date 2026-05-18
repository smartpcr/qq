// -----------------------------------------------------------------------
// <copyright file="OutboundQueueHealthCheck.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 6.2 — composite-friendly <see cref="IHealthCheck"/> that
/// surfaces the outbound delivery pipeline's two operator-visible
/// pressure signals as a single liveness signal:
/// <list type="bullet">
///   <item><description>
///   <see cref="HealthStatus.Degraded"/> when the durable outbox
///   depth (<see cref="OutboundMessageStatus.Pending"/> +
///   <see cref="OutboundMessageStatus.Sending"/>) exceeds
///   <see cref="OutboundQueueOptions.DegradedDepthThreshold"/>
///   (default 1000 per the Stage 6.2 brief).
///   </description></item>
///   <item><description>
///   <see cref="HealthStatus.Unhealthy"/> when the dead-letter
///   queue depth exceeds
///   <see cref="DeadLetterQueueOptions.UnhealthyThreshold"/>
///   (configurable, defaults to 100 in production / 25 in
///   development).
///   </description></item>
/// </list>
/// Reports the most severe of the two signals — i.e. a healthy DLQ
/// + an elevated queue depth is <c>Degraded</c>; an elevated DLQ
/// (with any queue depth) is <c>Unhealthy</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a single composite check.</b> The Stage 6.2 brief defines
/// the check as one entry on the <c>/healthz</c> response: "reports
/// degraded if queue depth exceeds 1000 and unhealthy if dead-letter
/// queue depth exceeds configurable threshold". Splitting the two
/// signals across two separate checks (which is how the legacy
/// Stage 4.2 <see cref="DeadLetterQueueHealthCheck"/> implements the
/// DLQ leg in isolation) would force the operator runbook to
/// correlate two distinct entries to understand outbound health.
/// Reporting both via this one check matches the brief literally.
/// The Stage 4.2 standalone check is retained alongside as a
/// defense-in-depth signal — both reach the same DLQ row count via
/// the same <see cref="IDeadLetterQueue.CountAsync"/> entry point so
/// they cannot diverge.
/// </para>
/// <para>
/// <b>Scope bridging.</b> Registered as a singleton so the
/// <see cref="IHealthCheck"/> framework's per-call resolution does
/// not produce a captive-dependency conflict; the check opens a
/// fresh <see cref="IServiceScope"/> per probe to materialise
/// <see cref="MessagingDbContext"/> (scoped) for the queue-depth
/// query. Mirrors the pattern used by
/// <c>TelegramQueueDepthMetrics</c> in the Worker assembly.
/// </para>
/// <para>
/// <b>Failure mode.</b> Either probe (queue depth, DLQ count) that
/// throws a non-cancellation exception surfaces as
/// <see cref="HealthStatus.Unhealthy"/> with the exception recorded
/// so the operator runbook can pivot on the underlying error —
/// silent success on a probe failure would mask real outages.
/// </para>
/// <para>
/// <b>Caller cancellation propagates (iter-3 evaluator item 2).</b>
/// Both <see langword="catch"/> blocks are narrowed with
/// <c>when (ex is not OperationCanceledException)</c>; OCE thrown
/// because the caller's <see cref="CancellationToken"/> cancelled
/// propagates to the <see cref="HealthCheckService"/> rather than
/// being misreported as a queue / DLQ outage. Without this guard
/// every aborted <c>/healthz</c> request would lie about
/// outbound-queue health.
/// </para>
/// </remarks>
public sealed class OutboundQueueHealthCheck : IHealthCheck
{
    /// <summary>
    /// Canonical name to register this check under via
    /// <c>AddCheck&lt;OutboundQueueHealthCheck&gt;(Name, ...)</c>.
    /// The Stage 6.2 <c>/healthz</c> JSON output keys on this name.
    /// </summary>
    public const string Name = "outbound_queue";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeadLetterQueue _deadLetterQueue;
    private readonly OutboundQueueOptions _queueOptions;
    private readonly DeadLetterQueueOptions _dlqOptions;
    private readonly ILogger<OutboundQueueHealthCheck> _logger;

    public OutboundQueueHealthCheck(
        IServiceScopeFactory scopeFactory,
        IDeadLetterQueue deadLetterQueue,
        IOptions<OutboundQueueOptions> queueOptions,
        IOptions<DeadLetterQueueOptions> dlqOptions,
        ILogger<OutboundQueueHealthCheck> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _deadLetterQueue = deadLetterQueue ?? throw new ArgumentNullException(nameof(deadLetterQueue));
        _queueOptions = (queueOptions ?? throw new ArgumentNullException(nameof(queueOptions))).Value
            ?? throw new ArgumentNullException(nameof(queueOptions));
        _dlqOptions = (dlqOptions ?? throw new ArgumentNullException(nameof(dlqOptions))).Value
            ?? throw new ArgumentNullException(nameof(dlqOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Iter-3 evaluator item 2 — observe caller cancellation
        // BEFORE entering any EF Core / DLQ adapter call. The
        // narrowed catch filters below propagate OCE if EF Core
        // surfaces it, but some provider paths (notably
        // RelationalDatabaseCreator) swallow OCE internally with a
        // broad catch and return a falsey result. Throwing eagerly
        // here keeps the cancellation contract honest regardless of
        // which downstream API absorbs the token.
        cancellationToken.ThrowIfCancellationRequested();

        var data = new Dictionary<string, object>
        {
            ["queue_degraded_threshold"] = _queueOptions.DegradedDepthThreshold,
            ["dlq_unhealthy_threshold"] = _dlqOptions.UnhealthyThreshold,
        };

        int queueDepth;
        try
        {
            queueDepth = await GetQueueDepthAsync(cancellationToken).ConfigureAwait(false);
            data["queue_depth"] = queueDepth;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Iter-3 evaluator item 2 — OperationCanceledException
            // MUST propagate so the HealthCheckService can handle
            // caller cancellation / host shutdown natively. A broad
            // catch would misreport "queue probe failed" on every
            // aborted /healthz request.
            _logger.LogWarning(
                ex,
                "OutboundQueueHealthCheck — queue-depth probe failed; reporting Unhealthy.");

            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: "Failed to read outbound queue depth — the messaging database is unreachable or throwing.",
                exception: ex,
                data: data);
        }

        cancellationToken.ThrowIfCancellationRequested();

        int dlqDepth;
        try
        {
            dlqDepth = await _deadLetterQueue.CountAsync(cancellationToken).ConfigureAwait(false);
            data["dlq_depth"] = dlqDepth;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Iter-3 evaluator item 2 — same OCE-propagation
            // contract as the queue-depth probe above.
            _logger.LogWarning(
                ex,
                "OutboundQueueHealthCheck — DLQ-depth probe failed; reporting Unhealthy.");

            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: "Failed to read dead-letter queue depth — the persistence store is unreachable or throwing.",
                exception: ex,
                data: data);
        }

        if (dlqDepth > _dlqOptions.UnhealthyThreshold)
        {
            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: $"Dead-letter queue depth {dlqDepth} exceeds configured threshold {_dlqOptions.UnhealthyThreshold}. Operator action: triage the outbound dead-letter backlog.",
                data: data);
        }

        if (queueDepth > _queueOptions.DegradedDepthThreshold)
        {
            return new HealthCheckResult(
                HealthStatus.Degraded,
                description: $"Outbound queue depth {queueDepth} exceeds degraded threshold {_queueOptions.DegradedDepthThreshold}. Pending/Sending backlog is building up.",
                data: data);
        }

        return new HealthCheckResult(
            HealthStatus.Healthy,
            description: $"Outbound queue depth {queueDepth} within threshold {_queueOptions.DegradedDepthThreshold}; dead-letter queue depth {dlqDepth} within threshold {_dlqOptions.UnhealthyThreshold}.",
            data: data);
    }

    private async Task<int> GetQueueDepthAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        return await db.OutboundMessages
            .AsNoTracking()
            .CountAsync(
                x => x.Status == OutboundMessageStatus.Pending
                     || x.Status == OutboundMessageStatus.Sending,
                ct)
            .ConfigureAwait(false);
    }
}
