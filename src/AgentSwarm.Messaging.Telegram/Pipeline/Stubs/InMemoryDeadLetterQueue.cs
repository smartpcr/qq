// -----------------------------------------------------------------------
// <copyright file="InMemoryDeadLetterQueue.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Pipeline.Stubs;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;

/// <summary>
/// Stage 4.2 — in-process <see cref="IDeadLetterQueue"/> fallback for
/// dev / unit-test bootstraps that have not wired the persistence
/// module. Mirrors the
/// <see cref="Persistence.PersistentDeadLetterQueue"/> contract:
/// idempotent on <see cref="OutboundMessage.MessageId"/>, returns
/// rows ordered by <see cref="DeadLetterMessage.DeadLetteredAt"/>,
/// and reports the non-acknowledged count via <see cref="CountAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacement contract.</b> Registered in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> via
/// <c>TryAddSingleton</c> so the Stage 4.2 production registration
/// (<c>Replace(Singleton&lt;IDeadLetterQueue, PersistentDeadLetterQueue&gt;)</c>
/// inside <see cref="Persistence.ServiceCollectionExtensions.AddMessagingPersistence"/>)
/// wins by last-wins semantics. Same fallback pattern as
/// <see cref="InMemoryOutboundDeadLetterStore"/>,
/// <see cref="InMemoryOutboundQueue"/>, etc.
/// </para>
/// <para>
/// <b>Idempotency.</b> A duplicate
/// <see cref="SendToDeadLetterAsync"/> with the same
/// <see cref="OutboundMessage.MessageId"/> is a no-op — the existing
/// ledger row wins. Mirrors the production persistent queue's
/// UNIQUE-index behaviour.
/// </para>
/// </remarks>
internal sealed class InMemoryDeadLetterQueue : IDeadLetterQueue
{
    private readonly ConcurrentDictionary<Guid, DeadLetterMessage> _rowsByOriginalMessageId
        = new();

    /// <inheritdoc />
    public Task SendToDeadLetterAsync(
        OutboundMessage message,
        FailureReason reason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var truncatedError = reason.FinalError.Length > 2048
            ? reason.FinalError.Substring(0, 2048)
            : reason.FinalError;

        var row = new DeadLetterMessage
        {
            Id = Guid.NewGuid(),
            OriginalMessageId = message.MessageId,
            IdempotencyKey = message.IdempotencyKey,
            ChatId = message.ChatId,
            Payload = message.Payload,
            SourceEnvelopeJson = message.SourceEnvelopeJson,
            Severity = message.Severity,
            SourceType = message.SourceType,
            SourceId = message.SourceId,
            AgentId = reason.AgentId ?? AgentIdExtractor.TryExtract(message),
            CorrelationId = message.CorrelationId,
            AttemptCount = reason.AttemptCount,
            FinalError = truncatedError,
            // Iter-2 evaluator item 1 — populate the architecture-mandated
            // AttemptTimestamps + ErrorHistory + ReplayStatus +
            // ReplayCorrelationId fields so the dev / test in-memory path
            // matches the persistent path's audit shape. ReplayStatus
            // defaults to None and ReplayCorrelationId to null at insert
            // (Stage 4.2 itself does not mutate these).
            AttemptTimestamps = reason.AttemptTimestampsJson,
            ErrorHistory = reason.ErrorHistoryJson,
            FailureCategory = reason.Category,
            AlertStatus = DeadLetterAlertStatus.Pending,
            ReplayStatus = DeadLetterReplayStatus.None,
            ReplayCorrelationId = null,
            DeadLetteredAt = reason.FailedAt,
            CreatedAt = message.CreatedAt,
        };

        // GetOrAdd — the existing row wins on a duplicate write so the
        // dev fallback matches the production UNIQUE-index semantics.
        _rowsByOriginalMessageId.GetOrAdd(message.MessageId, row);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(CancellationToken ct)
    {
        var snapshot = _rowsByOriginalMessageId.Values
            .OrderBy(x => x.DeadLetteredAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<DeadLetterMessage>>(snapshot);
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken ct)
    {
        var count = _rowsByOriginalMessageId.Values
            .Count(x => x.AlertStatus != DeadLetterAlertStatus.Acknowledged);
        return Task.FromResult(count);
    }

    /// <inheritdoc />
    public Task MarkAlertSentAsync(
        Guid originalMessageId,
        DateTimeOffset alertSentAt,
        CancellationToken ct)
    {
        // Iter-2 evaluator item 3 — flip Pending→Sent so the dev /
        // unit-test fallback mirrors the persistent queue's
        // AlertStatus transition. Records are immutable
        // (init-only properties), so we rebuild the row via `with`
        // and CAS-update; concurrent calls are tolerated.
        if (!_rowsByOriginalMessageId.TryGetValue(originalMessageId, out var existing))
        {
            // Missing row = no-op (DLQ insert race with recovery
            // sweep, or unit-test invoked the flip without inserting
            // first). The contract is idempotent.
            return Task.CompletedTask;
        }

        if (existing.AlertStatus != DeadLetterAlertStatus.Pending)
        {
            // Already Sent or Acknowledged — never regress.
            return Task.CompletedTask;
        }

        var updated = existing with
        {
            AlertStatus = DeadLetterAlertStatus.Sent,
            AlertSentAt = alertSentAt,
        };

        _rowsByOriginalMessageId.TryUpdate(originalMessageId, updated, existing);
        return Task.CompletedTask;
    }
}
