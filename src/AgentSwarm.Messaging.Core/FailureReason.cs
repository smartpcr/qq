// -----------------------------------------------------------------------
// <copyright file="FailureReason.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using System;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Stage 4.2 — strongly-typed failure context passed by the
/// <c>OutboundQueueProcessor</c> to
/// <see cref="IDeadLetterQueue.SendToDeadLetterAsync"/> when an
/// outbox row exhausts its retry budget or hits a permanent failure.
/// Replaces the prior "stringified reason" shape with a structured
/// record so the persistent dead-letter ledger does not have to
/// re-parse the failure category out of the message body.
/// </summary>
/// <param name="Category">
/// Discriminator for the failure mode — see
/// <see cref="OutboundFailureCategory"/>. Drives the operator
/// runbook branch on the dead-letter audit screen.
/// </param>
/// <param name="FinalError">
/// Free-form text of the last error observed by the sender. The
/// dead-letter ledger truncates this to 2048 chars at write time;
/// the caller does not need to pre-truncate.
/// </param>
/// <param name="AttemptCount">
/// Total delivery attempts consumed before the failure verdict.
/// Equals the outbox row's <c>AttemptCount</c> after the final
/// failure pass.
/// </param>
/// <param name="FailedAt">
/// UTC instant the processor declared the failure terminal. Used as
/// the dead-letter ledger row's <c>DeadLetteredAt</c> timestamp.
/// </param>
public readonly record struct FailureReason(
    OutboundFailureCategory Category,
    string FinalError,
    int AttemptCount,
    DateTimeOffset FailedAt)
{
    /// <summary>
    /// Iter-2 evaluator item 4 — logical agent identity associated
    /// with the dead-lettered send (e.g. the <c>AgentQuestion.AgentId</c>
    /// for a <c>Question</c> source or the
    /// <c>MessengerMessage.AgentId</c> for an <c>Alert</c> source).
    /// Persisted onto the <c>dead_letter_messages</c> row's
    /// <c>AgentId</c> column so e2e-scenarios.md "the dead-letter
    /// record includes CorrelationId, AgentId, message content, and
    /// failure reason" is satisfied without joining back to the
    /// outbox row or re-parsing <c>SourceEnvelopeJson</c>. Nullable
    /// because not every <c>OutboundSourceType</c> carries an agent
    /// identity (e.g. some <c>CommandAck</c> / <c>StatusUpdate</c>
    /// flows originate from a tenant boundary rather than a single
    /// agent); the dead-letter audit screen tolerates null here.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// Stage 4.2 iter-2 evaluator item 1 — JSON array of
    /// ISO-8601 UTC timestamps, one per attempt, mirroring
    /// architecture.md §3.1 line 386 (<c>AttemptTimestamps</c>). The
    /// producer (<c>OutboundQueueProcessor.HandleFailureAsync</c>)
    /// projects this from the outbox row's accumulated
    /// <c>AttemptHistoryJson</c> column. Defaults to
    /// <see cref="AttemptHistory.Empty"/> when omitted so a
    /// caller that constructs the record via the positional ctor
    /// without the new fields still produces a well-formed JSON
    /// array (rather than a NOT NULL violation on the DLQ insert).
    /// </summary>
    public string AttemptTimestampsJson { get; init; } = AttemptHistory.Empty;

    /// <summary>
    /// Stage 4.2 iter-2 evaluator item 1 — JSON array of
    /// <c>{"attempt", "timestamp", "error", "httpStatus"}</c>
    /// objects, one per attempt, mirroring architecture.md §3.1 line
    /// 388 (<c>ErrorHistory</c>). Same producer + default-value
    /// rationale as <see cref="AttemptTimestampsJson"/>.
    /// </summary>
    public string ErrorHistoryJson { get; init; } = AttemptHistory.Empty;

    /// <summary>
    /// Convenience constructor for a permanent failure where the
    /// attempt count is the row's <c>AttemptCount</c> after the
    /// final pass and the timestamp is sourced from a supplied
    /// <see cref="TimeProvider"/>.
    /// </summary>
    public static FailureReason From(
        OutboundFailureCategory category,
        string finalError,
        int attemptCount,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(finalError);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new FailureReason(category, finalError, attemptCount, timeProvider.GetUtcNow());
    }
}
