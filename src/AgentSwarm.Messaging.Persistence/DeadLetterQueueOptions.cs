// -----------------------------------------------------------------------
// <copyright file="DeadLetterQueueOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Stage 4.2 — options bound from the <c>DeadLetterQueue</c>
/// configuration section. Drives the
/// <see cref="DeadLetterQueueHealthCheck"/>'s unhealthy-threshold
/// poll and any future operator-facing dead-letter dashboards.
/// </summary>
/// <remarks>
/// <para>
/// The threshold is a soft signal — the health check reports the
/// host unhealthy when the live dead-letter count exceeds
/// <see cref="UnhealthyThreshold"/>, which causes orchestrators
/// (Kubernetes liveness probes, load balancers, deployment
/// gates) to surface the condition to operators. The dead-letter
/// queue continues to accept new rows past the threshold — the
/// signal is for human attention, not for backpressure.
/// </para>
/// </remarks>
public sealed class DeadLetterQueueOptions
{
    /// <summary>
    /// Canonical configuration section name. Bound from the
    /// <c>DeadLetterQueue</c> block in <c>appsettings.json</c>.
    /// </summary>
    public const string SectionName = "DeadLetterQueue";

    /// <summary>
    /// Dead-letter row count above which
    /// <see cref="DeadLetterQueueHealthCheck"/> reports the host
    /// <c>Unhealthy</c>. Default <c>100</c> — sized so a sustained
    /// burst of transient Telegram failures (~20 dead-letters per
    /// hour at a 5-attempt budget) takes ~5 hours to trip the
    /// alarm. Operators tune this for their environment's expected
    /// background dead-letter rate.
    /// </summary>
    public int UnhealthyThreshold { get; set; } = 100;
}
