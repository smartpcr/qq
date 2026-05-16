// -----------------------------------------------------------------------
// <copyright file="TelegramSendFailedException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Sending;

using System;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Thrown by <see cref="TelegramMessageSender"/> when a transient
/// Telegram Bot API failure (HTTP transport error, Telegram 5xx,
/// request timeout) has exhausted the in-sender retry budget. Carries
/// the chat id and correlation id of the failing send so the Stage 4.1
/// <c>OutboundQueueProcessor</c> can dead-letter the originating
/// outbox row with full context (and the optional
/// <see cref="Abstractions.IAlertService"/> can pivot directly into
/// the failed correlation in logs / traces).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated typed exception.</b> The iter-3 evaluator item 5
/// asked for durable retry + dead-letter behaviour for non-429 send
/// failures. The architecture places the canonical outbox + DLQ at
/// Stage 4.1's <c>OutboundQueueProcessor</c>, so the sender does not
/// itself enqueue / dead-letter outbox rows. Instead it surfaces the
/// exhaustion as a typed exception that Stage 4.1's processor
/// catches specifically — letting the processor map
/// <see cref="TelegramSendFailedException"/> directly to
/// <c>IOutboundQueue.DeadLetterAsync</c> +
/// <c>IAlertService.SendAlertAsync</c> without a fragile
/// "any exception means dead-letter" catch.
/// </para>
/// <para>
/// <b>Pre-stage 4.1 alert path.</b> When an
/// <see cref="Abstractions.IAlertService"/> is registered in the DI
/// container the sender invokes it directly before throwing, so even
/// before the Stage 4.1 outbox lands, the operator gets an out-of-band
/// "outbound send failed permanently" notification — meeting the
/// story's reliability acceptance criterion ("If Telegram send fails,
/// message is retried and eventually dead-lettered with alert") at
/// the sender boundary today.
/// </para>
/// </remarks>
public sealed class TelegramSendFailedException : Exception
{
    /// <summary>
    /// Telegram chat the failing send targeted.
    /// </summary>
    public long ChatId { get; }

    /// <summary>
    /// Correlation id of the failing send — non-empty by construction
    /// (the sender's <c>PrepareOutbound</c> guarantees a trace id even
    /// when the caller did not supply one).
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Number of attempts consumed before exhaustion. Equals
    /// <see cref="TelegramMessageSender.MaxTransientRetries"/> + 1
    /// (initial attempt + all retries) when
    /// <see cref="FailureCategory"/> is
    /// <see cref="OutboundFailureCategory.TransientTransport"/>, or
    /// <see cref="TelegramMessageSender.MaxRateLimitRetries"/> + 1
    /// when it is
    /// <see cref="OutboundFailureCategory.RateLimitExhausted"/>.
    /// </summary>
    public int AttemptCount { get; }

    /// <summary>
    /// Why the sender gave up. Lets Stage 4.1's
    /// <c>OutboundQueueProcessor</c> distinguish a flood-controlled
    /// 429 exhaustion (iter-4 evaluator item 5) from a transient
    /// transport / 5xx exhaustion when deciding whether to wait
    /// longer before retrying the outbox row.
    /// </summary>
    public OutboundFailureCategory FailureCategory { get; }

    /// <summary>
    /// Iter-5 evaluator item 4 — <see langword="true"/> when the
    /// sender successfully persisted an
    /// <see cref="OutboundDeadLetterRecord"/> row to
    /// <c>IOutboundDeadLetterStore</c> before throwing this exception;
    /// <see langword="false"/> when every persistence attempt failed
    /// and only the alert channel observed the dead-letter event.
    /// Stage 4.1's <c>OutboundQueueProcessor</c> can use this flag to
    /// decide whether to issue its own corrective durability write
    /// (when <see langword="false"/>) or trust the row already exists
    /// (when <see langword="true"/>). The flag never overstates
    /// durability: if it is true, the row IS in the database.
    /// </summary>
    public bool DeadLetterPersisted { get; }

    public TelegramSendFailedException(
        long chatId,
        string correlationId,
        int attemptCount,
        OutboundFailureCategory failureCategory,
        bool deadLetterPersisted,
        string message,
        Exception inner)
        : base(message, inner)
    {
        ChatId = chatId;
        CorrelationId = correlationId;
        AttemptCount = attemptCount;
        FailureCategory = failureCategory;
        DeadLetterPersisted = deadLetterPersisted;
    }
}
