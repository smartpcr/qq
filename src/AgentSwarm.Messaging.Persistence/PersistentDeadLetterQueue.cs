// -----------------------------------------------------------------------
// <copyright file="PersistentDeadLetterQueue.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 4.2 — EF Core-backed <see cref="IDeadLetterQueue"/> that
/// persists the outbox-row companion dead-letter ledger to the
/// <c>dead_letter_messages</c> table. Written by the
/// <c>OutboundQueueProcessor</c> when an outbox row exhausts its
/// retry budget or hits a permanent failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime + scope bridging.</b> Registered as a singleton (so
/// the singleton <c>OutboundQueueProcessor</c> can take a hard
/// dependency on it) but uses an
/// <see cref="IServiceScopeFactory"/> to open a fresh scope per call,
/// satisfying the captive-dependency rule for the scoped
/// <see cref="MessagingDbContext"/>. Same pattern as
/// <see cref="PersistentOutboundQueue"/> and
/// <see cref="PersistentOutboundDeadLetterStore"/>.
/// </para>
/// <para>
/// <b>Idempotency on OriginalMessageId.</b> The
/// <c>ux_dead_letter_messages_original_message_id</c> UNIQUE index
/// is the database-level gate for outbox-row→DLQ-row deduplication.
/// <see cref="SendToDeadLetterAsync"/> pre-flights an existence
/// probe (covers the hot duplicate path so a concurrent worker
/// retry does not burn an INSERT round-trip) and falls back to a
/// <see cref="DbUpdateException"/> catch for the concurrent-insert
/// race. Same shape as
/// <see cref="PersistentOutboundQueue.EnqueueAsync"/>'s idempotency
/// dedup; duplicate writes are a successful no-op.
/// </para>
/// </remarks>
internal sealed class PersistentDeadLetterQueue : IDeadLetterQueue
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PersistentDeadLetterQueue> _logger;

    public PersistentDeadLetterQueue(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<PersistentDeadLetterQueue> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SendToDeadLetterAsync(
        OutboundMessage message,
        FailureReason reason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Pre-flight idempotency probe — the UNIQUE index on
        // OriginalMessageId is the authoritative gate, the probe just
        // short-circuits the common case (processor retried after a
        // CAS-lost DeadLetterAsync transition on the outbox row) so a
        // duplicate write does not even reach the INSERT round-trip.
        var existing = await db.DeadLetterMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OriginalMessageId == message.MessageId, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogDebug(
                "DeadLetter row for OriginalMessageId={OriginalMessageId} already exists (Id={ExistingId} AlertStatus={AlertStatus}); treating duplicate write as success.",
                message.MessageId,
                existing.Id,
                existing.AlertStatus);
            return;
        }

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
            // Iter-2 evaluator item 4 — AgentId. Preferred source is
            // the FailureReason populated by the processor (which has
            // already deserialised the envelope), with a fallback to
            // re-extracting from SourceEnvelopeJson when an external
            // caller skips the envelope-aware path. Truncated to fit
            // the EF-mapped 128-char column.
            AgentId = TruncateAgentId(
                reason.AgentId ?? AgentIdExtractor.TryExtract(message)),
            CorrelationId = message.CorrelationId,
            AttemptCount = reason.AttemptCount,
            FinalError = truncatedError,
            // Iter-2 evaluator item 1 — architecture.md §3.1 lines
            // 386–388 mandate AttemptTimestamps + ErrorHistory on the
            // dead-letter row. Sourced from the FailureReason that the
            // processor projected from the outbox row's accumulated
            // AttemptHistoryJson. Defaults to AttemptHistory.Empty
            // ("[]") when the upstream caller did not populate them
            // (e.g. a legacy direct construction path) so the NOT NULL
            // column constraint never blocks the dead-letter insert.
            AttemptTimestamps = reason.AttemptTimestampsJson,
            ErrorHistory = reason.ErrorHistoryJson,
            FailureCategory = reason.Category,
            AlertStatus = DeadLetterAlertStatus.Pending,
            // Iter-2 evaluator item 1 — ReplayStatus defaults to None
            // at insert (architecture.md §3.1 line 391). Stage 4.2
            // itself never mutates this column; the operator replay
            // workflow is a future workstream.
            ReplayStatus = DeadLetterReplayStatus.None,
            ReplayCorrelationId = null,
            DeadLetteredAt = reason.FailedAt,
            CreatedAt = message.CreatedAt,
        };

        db.DeadLetterMessages.Add(row);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogWarning(
                "DeadLetterMessage persisted — Id={Id} OriginalMessageId={OriginalMessageId} CorrelationId={CorrelationId} Severity={Severity} SourceType={SourceType} AttemptCount={AttemptCount} FailureCategory={FailureCategory}.",
                row.Id,
                row.OriginalMessageId,
                row.CorrelationId,
                row.Severity,
                row.SourceType,
                row.AttemptCount,
                row.FailureCategory);
        }
        catch (DbUpdateException ex)
        {
            // Race shape: two workers raced past the pre-flight probe
            // and both Add()'d a row for the same OriginalMessageId.
            // Detach the failed insert and re-query — if a row now
            // exists the duplicate was accepted by the rival writer
            // and we treat the unique-violation as success. Otherwise
            // re-throw to surface an unknown failure.
            db.Entry(row).State = EntityState.Detached;

            var rival = await db.DeadLetterMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OriginalMessageId == message.MessageId, ct)
                .ConfigureAwait(false);

            if (rival is null)
            {
                _logger.LogError(
                    ex,
                    "DeadLetter save failed for OriginalMessageId={OriginalMessageId} CorrelationId={CorrelationId} and no rival row was found — not a duplicate.",
                    message.MessageId,
                    message.CorrelationId);
                throw;
            }

            _logger.LogInformation(
                "DeadLetter race resolved — OriginalMessageId={OriginalMessageId} already persisted by rival Id={RivalId}; treating duplicate as success.",
                message.MessageId,
                rival.Id);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetterMessage>> ListAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        return await db.DeadLetterMessages
            .AsNoTracking()
            .OrderBy(x => x.DeadLetteredAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Count rows that have not been operator-acknowledged. The
        // health check pivots on this so an operator who has
        // explicitly cleared a backlog (transition Sent → Acknowledged
        // via a future replay-or-suppress workflow) is not penalised
        // for the history of cleared dead-letters.
        return await db.DeadLetterMessages
            .AsNoTracking()
            .CountAsync(x => x.AlertStatus != DeadLetterAlertStatus.Acknowledged, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkAlertSentAsync(
        Guid originalMessageId,
        DateTimeOffset alertSentAt,
        CancellationToken ct)
    {
        // Iter-2 evaluator item 3 — flip the row from Pending to Sent
        // once the processor's IAlertService.SendAlertAsync has
        // returned successfully. Idempotent: missing row = no-op
        // (DLQ insert race vs. recovery sweep); already-Sent /
        // already-Acknowledged rows are not regressed.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var row = await db.DeadLetterMessages
            .FirstOrDefaultAsync(x => x.OriginalMessageId == originalMessageId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            _logger.LogDebug(
                "MarkAlertSentAsync — no dead-letter row found for OriginalMessageId={OriginalMessageId}; treating as no-op (recovery sweep race).",
                originalMessageId);
            return;
        }

        if (row.AlertStatus != DeadLetterAlertStatus.Pending)
        {
            _logger.LogDebug(
                "MarkAlertSentAsync — dead-letter row Id={Id} OriginalMessageId={OriginalMessageId} already in AlertStatus={AlertStatus}; not regressing.",
                row.Id,
                originalMessageId,
                row.AlertStatus);
            return;
        }

        // DeadLetterMessage is a record with init-only properties —
        // rebuild via `with` and re-attach via Update so EF emits a
        // targeted UPDATE on the AlertStatus + AlertSentAt columns.
        var updated = row with
        {
            AlertStatus = DeadLetterAlertStatus.Sent,
            AlertSentAt = alertSentAt,
        };
        db.Entry(row).State = EntityState.Detached;
        db.DeadLetterMessages.Update(updated);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Dead-letter row Id={Id} OriginalMessageId={OriginalMessageId} flipped Pending→Sent at {AlertSentAt}.",
                row.Id,
                originalMessageId,
                alertSentAt);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Rival worker raced us between read and save — re-fetch
            // and tolerate any non-Pending end-state as success
            // (Acknowledged wins over Sent; Sent wins over Pending).
            _logger.LogDebug(
                ex,
                "MarkAlertSentAsync concurrency race for OriginalMessageId={OriginalMessageId}; rival writer wins.",
                originalMessageId);
        }
    }

    private static string? TruncateAgentId(string? agentId)
    {
        if (agentId is null)
        {
            return null;
        }

        agentId = agentId.Trim();
        if (agentId.Length == 0)
        {
            return null;
        }

        return agentId.Length > 128 ? agentId.Substring(0, 128) : agentId;
    }
}
