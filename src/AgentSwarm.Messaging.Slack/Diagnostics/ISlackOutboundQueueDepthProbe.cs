// -----------------------------------------------------------------------
// <copyright file="ISlackOutboundQueueDepthProbe.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

/// <summary>
/// Optional cheap-to-call depth accessor implemented by Slack outbound
/// queue backends so the Stage 7.3 health-check pipeline can sample
/// queue depth without dequeuing or draining envelopes.
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// "Register health check for outbound queue depth: report
/// <c>Degraded</c> if queue depth exceeds a configurable threshold
/// (default 1000)."
/// </para>
/// <para>
/// The interface is deliberately separate from
/// <see cref="Queues.ISlackOutboundQueue"/> so a backend that cannot
/// answer the question cheaply (or at all) simply does not implement
/// it. The health check
/// (<see cref="SlackOutboundQueueDepthHealthCheck"/>) treats a
/// concrete queue that does not implement this probe as "Healthy with
/// unknown depth" -- a safe default that does not falsely degrade the
/// readiness signal.
/// </para>
/// <para>
/// Implementations MUST return a non-negative integer and MUST NOT
/// block on I/O for more than a few milliseconds -- the health check
/// runs on a hot path inspected by Kubernetes liveness / readiness
/// probes.
/// </para>
/// </remarks>
public interface ISlackOutboundQueueDepthProbe
{
    /// <summary>
    /// Returns the current number of buffered outbound envelopes that
    /// have not yet been delivered to Slack (in-memory channel count
    /// for the channel-based queue; pending journal-file count for
    /// the file-system queue).
    /// </summary>
    /// <returns>
    /// A non-negative depth. Implementations that hit a transient
    /// inspection failure SHOULD return <c>0</c> rather than throwing
    /// so the health check never crashes the readiness probe.
    /// </returns>
    int GetCurrentDepth();
}
