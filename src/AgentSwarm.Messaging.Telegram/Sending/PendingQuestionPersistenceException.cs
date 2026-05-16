// -----------------------------------------------------------------------
// <copyright file="PendingQuestionPersistenceException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Sending;

/// <summary>
/// Thrown by
/// <see cref="TelegramMessageSender.SendQuestionAsync"/> when the
/// Telegram send succeeded but the subsequent
/// <see cref="IPendingQuestionStore.StoreAsync"/> call failed. Per
/// Stage 3.5 evaluator iter-1 item 6, the pending-question row is
/// load-bearing for the callback handler and the timeout sweep, so a
/// silent swallow would leave the operator's tap unresolvable and
/// the timeout default-action unfireable.
/// </summary>
/// <remarks>
/// <para>
/// The exception carries the
/// <see cref="AgentQuestion.QuestionId"/>,
/// <see cref="TelegramChatId"/>,
/// <see cref="TelegramMessageId"/>, and
/// <see cref="AgentQuestion.CorrelationId"/> that a recovery process
/// needs to reconstruct the pending-question row from the audit
/// trail. The Telegram message has already been DELIVERED — the
/// caller (typically the Stage 4.1 OutboundQueueProcessor) should
/// NOT re-send the message; it should call
/// <see cref="IPendingQuestionStore.StoreAsync"/> directly with the
/// recovered envelope (the store's upsert semantics on
/// <see cref="AgentQuestion.QuestionId"/> make a second attempt
/// idempotent).
/// </para>
/// </remarks>
public sealed class PendingQuestionPersistenceException : Exception
{
    public PendingQuestionPersistenceException(
        string questionId,
        long telegramChatId,
        long telegramMessageId,
        string correlationId,
        Exception innerException,
        bool rateLimited = false)
        : base(
            $"Failed to persist pending question '{questionId}' after a successful Telegram send to chat {telegramChatId} (message {telegramMessageId}). The Telegram message has been DELIVERED; do not re-send. Use IPendingQuestionStore.StoreAsync directly to backfill the row.",
            innerException)
    {
        this.QuestionId = questionId;
        this.TelegramChatId = telegramChatId;
        this.TelegramMessageId = telegramMessageId;
        this.CorrelationId = correlationId;
        this.RateLimited = rateLimited;
    }

    /// <summary>
    /// <see cref="AgentQuestion.QuestionId"/> of the question whose
    /// persistence failed.
    /// </summary>
    public string QuestionId { get; }

    /// <summary>
    /// Telegram chat ID the message was successfully delivered to.
    /// </summary>
    public long TelegramChatId { get; }

    /// <summary>
    /// Telegram message ID that the Bot API acknowledged for the
    /// already-delivered question; pair this with
    /// <see cref="TelegramChatId"/> to identify the message uniquely.
    /// </summary>
    public long TelegramMessageId { get; }

    /// <summary>
    /// <see cref="AgentQuestion.CorrelationId"/> of the original
    /// question, for trace correlation across the recovery path.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// <b>Stage 4.1 iter-2 evaluator item 1.</b>
    /// <see langword="true"/> when the Telegram send sequence that
    /// preceded the persistence failure incurred at least one 429
    /// flood-control retry. Mirrors
    /// <see cref="SendResult.RateLimited"/>; the Stage 4.1
    /// <c>OutboundQueueProcessor</c>'s recovery path uses this flag
    /// to scope <c>telegram.send.first_attempt_latency_ms</c> per
    /// architecture.md §10.4 — a recovered first-attempt send whose
    /// underlying wire path waited on retry_after MUST be excluded
    /// from the SLO histogram (it would have crossed 2 s on the
    /// retry_after alone) while still contributing to
    /// <c>telegram.send.all_attempts_latency_ms</c> for capacity
    /// planning.
    /// </summary>
    public bool RateLimited { get; }
}
