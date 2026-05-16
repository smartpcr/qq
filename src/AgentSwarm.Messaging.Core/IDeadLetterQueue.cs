// -----------------------------------------------------------------------
// <copyright file="IDeadLetterQueue.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Stage 4.2 — durable dead-letter queue for outbound Telegram
/// messages whose <see cref="OutboundMessage.MaxAttempts"/> budget
/// has been exhausted or that hit a permanent failure category
/// (<see cref="OutboundFailureCategory.Permanent"/>). Defined in
/// <c>AgentSwarm.Messaging.Core</c> per the Stage 4.2 brief
/// ("Define <c>IDeadLetterQueue</c> interface in Core"). The Stage
/// 4.1 <c>OutboundQueueProcessor</c> calls
/// <see cref="SendToDeadLetterAsync"/> before flipping the outbox row
/// to <see cref="OutboundMessageStatus.DeadLettered"/> so the
/// operator audit screen has a 1-to-1 outbox→DLQ mapping with the
/// full failure context, and so the secondary alert channel
/// (<see cref="IAlertService"/>) has a stable reference for follow-up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relationship to <see cref="IOutboundDeadLetterStore"/>.</b>
/// <see cref="IOutboundDeadLetterStore"/> is the sender-side ledger
/// written by <c>TelegramMessageSender</c> on in-sender retry
/// exhaustion (keyed by <c>(ChatId, CorrelationId)</c>);
/// <see cref="IDeadLetterQueue"/> is the queue-side ledger written
/// by the <c>OutboundQueueProcessor</c> when an outbox row's
/// retry budget is exhausted (keyed by
/// <see cref="DeadLetterMessage.OriginalMessageId"/>). Both ledgers
/// co-exist by design — see <see cref="DeadLetterMessage"/> remarks.
/// </para>
/// <para>
/// <b>Idempotency.</b> A duplicate
/// <see cref="SendToDeadLetterAsync"/> with the same
/// <see cref="OutboundMessage.MessageId"/> is a no-op rather than a
/// duplicate-key throw — the processor may retry the dead-letter
/// path after a CAS-lost transition on the outbox row, and a
/// duplicate must not poison the operator audit screen.
/// </para>
/// </remarks>
public interface IDeadLetterQueue
{
    /// <summary>
    /// Persist a dead-letter ledger row for the supplied
    /// <paramref name="message"/> using the failure context in
    /// <paramref name="reason"/>. Idempotent on
    /// <see cref="OutboundMessage.MessageId"/> — a second call with
    /// the same id is a successful no-op (the existing ledger row is
    /// preserved). The caller (<c>OutboundQueueProcessor</c>) MUST
    /// also call <see cref="IAlertService.SendAlertAsync"/> on a
    /// secondary channel after this method returns successfully so
    /// the on-call operator is notified out-of-band.
    /// </summary>
    Task SendToDeadLetterAsync(
        OutboundMessage message,
        FailureReason reason,
        CancellationToken ct);

    /// <summary>
    /// List every dead-letter row currently in the queue, ordered by
    /// <see cref="DeadLetterMessage.DeadLetteredAt"/> ASC (oldest
    /// first). Operator audit screens consume this for the "all
    /// dead-letters" view; the health-check loop uses
    /// <see cref="CountAsync"/> instead so a large queue does not
    /// pay the materialisation cost on every poll.
    /// </summary>
    Task<IReadOnlyList<DeadLetterMessage>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Return the current dead-letter queue depth — the count of
    /// rows whose <see cref="DeadLetterMessage.AlertStatus"/> is
    /// <see cref="DeadLetterAlertStatus.Pending"/> or
    /// <see cref="DeadLetterAlertStatus.Sent"/> (any non-acknowledged
    /// row counts as queue pressure). The Stage 4.2
    /// <c>DeadLetterQueueHealthCheck</c> reads this on every health
    /// poll and reports the host unhealthy when the count exceeds the
    /// configured threshold.
    /// </summary>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>
    /// Iter-2 evaluator item 3 — flip the dead-letter row identified
    /// by <paramref name="originalMessageId"/> from
    /// <see cref="DeadLetterAlertStatus.Pending"/> to
    /// <see cref="DeadLetterAlertStatus.Sent"/> and stamp
    /// <see cref="DeadLetterMessage.AlertSentAt"/> with
    /// <paramref name="alertSentAt"/>. Called by the
    /// <c>OutboundQueueProcessor</c> immediately after
    /// <see cref="IAlertService.SendAlertAsync"/> returns successfully
    /// so the persisted ledger reflects the alert outcome rather
    /// than leaving rows pinned at <c>Pending</c> forever.
    /// Idempotent: a no-op if no row matches
    /// <paramref name="originalMessageId"/> (the DLQ insert raced
    /// with a recovery sweep) or if the row's
    /// <see cref="DeadLetterMessage.AlertStatus"/> is already
    /// <see cref="DeadLetterAlertStatus.Sent"/> or
    /// <see cref="DeadLetterAlertStatus.Acknowledged"/>. Never
    /// downgrades a row out of <c>Acknowledged</c>.
    /// </summary>
    Task MarkAlertSentAsync(
        Guid originalMessageId,
        DateTimeOffset alertSentAt,
        CancellationToken ct);
}
