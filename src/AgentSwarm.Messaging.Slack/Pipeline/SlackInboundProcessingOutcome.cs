// -----------------------------------------------------------------------
// <copyright file="SlackInboundProcessingOutcome.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

/// <summary>
/// Discriminator returned by
/// <see cref="SlackInboundProcessingPipeline.ProcessAsync"/> so the
/// hosting <see cref="SlackInboundIngestor"/> (and tests) can assert on
/// the terminal status of an envelope without inspecting the audit
/// table.
/// </summary>
/// <remarks>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </remarks>
internal enum SlackInboundProcessingOutcome
{
    /// <summary>
    /// Authorization passed, idempotency claimed, handler returned
    /// successfully. <c>processing_status</c> on the inbound request
    /// row is <see cref="SlackInboundRequestProcessingStatus.Completed"/>.
    /// </summary>
    Processed = 0,

    /// <summary>
    /// The envelope was a duplicate (its idempotency key already
    /// existed in the dedup table) and was silently dropped. Per
    /// architecture.md §5.4 a duplicate triggers an audit row with
    /// <c>outcome = duplicate</c> but no handler invocation.
    /// </summary>
    Duplicate = 1,

    /// <summary>
    /// The authorization gate rejected the envelope before any
    /// idempotency or handler work happened. A
    /// <c>outcome = rejected_auth</c> audit row was written.
    /// </summary>
    Unauthorized = 2,

    /// <summary>
    /// The handler kept throwing transient errors and the retry
    /// budget was exhausted; the envelope was forwarded to
    /// <see cref="Queues.ISlackDeadLetterQueue"/>, the dedup row was
    /// stamped <see cref="SlackInboundRequestProcessingStatus.Failed"/>,
    /// and an <c>outcome = error</c> audit row was written.
    /// </summary>
    DeadLettered = 3,

    /// <summary>
    /// The handler threw a non-retriable exception (e.g.
    /// <see cref="System.OperationCanceledException"/> on shutdown,
    /// or an argument validation failure). The envelope was NOT sent
    /// to the DLQ; the dedup row is left in
    /// <see cref="SlackInboundRequestProcessingStatus.Processing"/>
    /// for an operator to triage.
    /// </summary>
    Skipped = 4,
}
