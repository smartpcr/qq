// -----------------------------------------------------------------------
// <copyright file="ISlackDeadLetterQueueDepthProbe.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

/// <summary>
/// Optional cheap-to-call depth accessor implemented by Slack
/// dead-letter queue backends so the Stage 7.3 health-check pipeline
/// can sample DLQ depth without scanning the underlying durable
/// surface on every probe invocation.
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// "Register health check for DLQ depth: report <c>Unhealthy</c> if
/// DLQ depth exceeds a configurable threshold (default 100)."
/// </para>
/// <para>
/// The interface is deliberately separate from
/// <see cref="Queues.ISlackDeadLetterQueue"/>'s
/// <see cref="Queues.ISlackDeadLetterQueue.InspectAsync(System.Threading.CancellationToken)"/>
/// because <c>InspectAsync</c> reconstructs every entry from the
/// underlying store (which, for
/// <see cref="Queues.FileSystemSlackDeadLetterQueue"/>, parses every
/// JSONL line); a Kubernetes probe firing every few seconds must not
/// pay that cost. Backends that cannot answer the question cheaply
/// simply do not implement this probe and the health check falls
/// back to a single conservative <c>InspectAsync</c> call.
/// </para>
/// </remarks>
public interface ISlackDeadLetterQueueDepthProbe
{
    /// <summary>
    /// Returns the current number of dead-lettered envelopes held by
    /// the queue (in-memory entry count for the
    /// <c>InMemorySlackDeadLetterQueue</c>; line count of the JSONL
    /// file for <c>FileSystemSlackDeadLetterQueue</c>).
    /// </summary>
    /// <returns>
    /// A non-negative depth. Implementations that hit a transient
    /// inspection failure SHOULD return <c>0</c> rather than throwing
    /// so the health check never crashes the readiness probe.
    /// </returns>
    int GetCurrentDepth();
}
