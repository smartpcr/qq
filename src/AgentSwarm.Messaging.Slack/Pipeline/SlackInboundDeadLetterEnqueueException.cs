// -----------------------------------------------------------------------
// <copyright file="SlackInboundDeadLetterEnqueueException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;

/// <summary>
/// Thrown by <see cref="SlackInboundProcessingPipeline"/> when an
/// envelope has exhausted its retry budget AND the configured
/// <see cref="Queues.ISlackDeadLetterQueue"/> backend rejected the
/// enqueue attempt.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Per the brief, exhausted envelopes <em>must</em> be moved to the
/// DLQ; silently dropping the payload when the DLQ backend is
/// temporarily unavailable would lose evidence of the poison
/// message and prevent operator triage. This exception lifts the
/// failure up to <see cref="SlackInboundIngestor"/> so it lands in
/// the outer error log and -- depending on the queue implementation
/// -- causes the inbound queue to redeliver the envelope on its own
/// retry schedule.
/// </para>
/// <para>
/// When this exception is thrown the dedup row is intentionally left
/// in <see cref="SlackInboundRequestProcessingStatus.Processing"/>
/// (i.e. neither <c>completed</c> nor <c>failed</c>). That state is
/// the signal to operators that the envelope is "stuck mid-flow"
/// and needs manual recovery once the DLQ backend is healthy.
/// </para>
/// </remarks>
internal sealed class SlackInboundDeadLetterEnqueueException : Exception
{
    public SlackInboundDeadLetterEnqueueException(
        string idempotencyKey,
        int attemptCount,
        Exception innerException)
        : base(BuildMessage(idempotencyKey, attemptCount), innerException)
    {
        this.IdempotencyKey = idempotencyKey ?? string.Empty;
        this.AttemptCount = attemptCount;
    }

    /// <summary>
    /// Gets the idempotency key of the envelope whose DLQ enqueue
    /// failed. Carried explicitly so the ingestor's outer log line
    /// can correlate this failure with subsequent Slack retries.
    /// </summary>
    public string IdempotencyKey { get; }

    /// <summary>
    /// Gets the number of handler attempts that ran before the
    /// pipeline gave up and tried to DLQ. Surfaces in alerting so
    /// operators can distinguish a handler that always throws
    /// (high attempt count) from a handler that succeeded but
    /// post-processing failed (lower count).
    /// </summary>
    public int AttemptCount { get; }

    private static string BuildMessage(string idempotencyKey, int attemptCount)
    {
        return $"Slack inbound dead-letter enqueue failed for idempotency_key='{idempotencyKey}' after {attemptCount} attempts; the dedup row was left in 'processing' state for operator recovery.";
    }
}
