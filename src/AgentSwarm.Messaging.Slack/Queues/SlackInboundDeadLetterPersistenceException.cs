// -----------------------------------------------------------------------
// <copyright file="SlackInboundDeadLetterPersistenceException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Queues;

using System;

/// <summary>
/// Thrown by <see cref="FileSystemSlackDeadLetterQueue"/> when an
/// append to the durable JSONL store fails (disk full, permission
/// revoked, network share dropped, file locked, etc.) so the
/// pipeline / ingestor can engage their last-resort fallback paths.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 iter 8 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>,
/// added to close evaluator item #1 ("FileSystemSlackDeadLetterQueue.EnqueueAsync
/// swallows persistence failures and returns success ... Propagate
/// DLQ persistence failures so SlackInboundDeadLetterEnqueueException
/// and the ingestor fallback path can preserve the envelope instead
/// of silently losing it"). The typed wrapper makes the failure
/// contract explicit and greppable -- callers can pattern-match on
/// the exception type when they need a different recovery shape
/// than the generic <see cref="System.IO.IOException"/> umbrella
/// would allow.
/// </para>
/// <para>
/// Propagation flow:
/// <list type="number">
///   <item>
///     <description>
///       <see cref="FileSystemSlackDeadLetterQueue.EnqueueAsync"/>
///       logs <see cref="Microsoft.Extensions.Logging.LogLevel.Critical"/>
///       for operator visibility, then throws this exception.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="Pipeline.SlackInboundProcessingPipeline"/>
///       catches it in its DLQ-enqueue try/catch and wraps it as
///       <see cref="Pipeline.SlackInboundDeadLetterEnqueueException"/>,
///       leaving the dedup row in <c>processing</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="Pipeline.SlackInboundIngestor"/> catches
///       <see cref="Pipeline.SlackInboundDeadLetterEnqueueException"/>
///       and forwards the envelope to
///       <see cref="Transport.ISlackInboundEnqueueDeadLetterSink"/>
///       (the bounded ring-buffer fallback), preserving
///       at-least-once delivery semantics.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class SlackInboundDeadLetterPersistenceException : Exception
{
    public SlackInboundDeadLetterPersistenceException(
        string filePath,
        Guid entryId,
        Exception innerException)
        : base(BuildMessage(filePath, entryId), innerException)
    {
        this.FilePath = filePath ?? string.Empty;
        this.EntryId = entryId;
    }

    /// <summary>
    /// Gets the absolute path of the JSONL file the append targeted.
    /// Surfaces in operator logs so the alert points to the exact
    /// file that needs reconciliation.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the <see cref="SlackDeadLetterEntry.EntryId"/> of the
    /// envelope that failed to persist. Carried explicitly so the
    /// ingestor's fallback log line can correlate this failure with
    /// the original poison message.
    /// </summary>
    public Guid EntryId { get; }

    private static string BuildMessage(string filePath, Guid entryId)
    {
        return $"FileSystemSlackDeadLetterQueue could not persist entry_id={entryId:D} to '{filePath}'. The envelope is being forwarded to the last-resort inbound-enqueue fallback sink to preserve at-least-once delivery.";
    }
}
