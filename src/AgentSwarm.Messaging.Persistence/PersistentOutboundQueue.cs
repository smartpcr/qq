// -----------------------------------------------------------------------
// <copyright file="PersistentOutboundQueue.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 4.1 — EF Core-backed <see cref="IOutboundQueue"/> that
/// persists outbound Telegram messages to the <c>outbox</c> table.
/// Drains in severity-priority order
/// (<see cref="MessageSeverity.Critical"/> &gt;
/// <see cref="MessageSeverity.High"/> &gt;
/// <see cref="MessageSeverity.Normal"/> &gt;
/// <see cref="MessageSeverity.Low"/>); a freshly-enqueued message
/// survives a worker restart and is replayable by the recovery sweep.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime + scope bridging.</b> Registered as a singleton (so the
/// connector's singleton <c>TelegramMessengerConnector</c> can take a
/// hard dependency on it) but uses an <see cref="IServiceScopeFactory"/>
/// to open a fresh scope per call, satisfying the captive-dependency
/// rule for the scoped <see cref="MessagingDbContext"/>. Same pattern
/// as <see cref="PersistentOutboundDeadLetterStore"/> and
/// <see cref="PersistentOperatorRegistry"/>.
/// </para>
/// <para>
/// <b>Severity-priority dequeue.</b> The dequeue query is
/// <c>WHERE Status = 'Pending' AND (NextRetryAt IS NULL OR NextRetryAt &lt;= now)
/// ORDER BY Severity ASC, CreatedAt ASC LIMIT 1</c>. The
/// <see cref="OutboundMessageConfiguration"/> persists
/// <see cref="MessageSeverity"/> as its underlying int
/// (<c>Critical=0, High=1, Normal=2, Low=3</c>) so ascending sort
/// yields the priority order. The composite index
/// <c>ix_outbox_status_severity_created</c> covers the query so the
/// dequeue is O(log n) regardless of outbox size.
/// </para>
/// <para>
/// <b>Atomic claim.</b> Two workers polling the same outbox MUST NOT
/// both observe the same message as <c>Pending</c>. The atomic claim
/// pattern is a single
/// <c>UPDATE outbox SET Status='Sending' WHERE MessageId=@id AND Status='Pending'</c>
/// emitted via EF Core 8's <c>ExecuteUpdateAsync</c> — provider-native
/// and row-level atomic under SQLite, PostgreSQL, and SQL Server.
/// The query above selects a candidate id; the second statement
/// arbitrates the claim. Worker A loses the CAS → loops to pick the
/// next candidate; Worker B wins and proceeds to send.
/// </para>
/// <para>
/// <b>Backpressure.</b> Per architecture.md §10.4, when the queue
/// depth (Pending + Sending) exceeds
/// <see cref="OutboundQueueOptions.MaxQueueDepth"/> (default 5000),
/// <see cref="MessageSeverity.Low"/>-severity enqueues are
/// dead-lettered immediately with
/// <see cref="OutboundQueueOptions.BackpressureDeadLetterReason"/> and
/// the canonical <c>telegram.messages.backpressure_dlq</c> counter is
/// incremented. <see cref="MessageSeverity.Normal"/>,
/// <see cref="MessageSeverity.High"/>, and
/// <see cref="MessageSeverity.Critical"/> messages are always
/// accepted regardless of depth.
/// </para>
/// <para>
/// <b>Idempotency dedup.</b> The <c>ux_outbox_idempotency_key</c>
/// UNIQUE index is the source of truth for outbound deduplication.
/// <see cref="EnqueueAsync(OutboundMessage, CancellationToken)"/>
/// pre-flights an existence probe on the hot duplicate path and falls
/// back to a <see cref="DbUpdateException"/> catch for the concurrent-
/// insert race. A duplicate is treated as success — the at-least-once
/// + dedup contract per architecture.md §3.1.
/// </para>
/// </remarks>
public sealed class PersistentOutboundQueue : IOutboundQueue
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboundQueueOptions _options;
    private readonly OutboundQueueMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PersistentOutboundQueue> _logger;

    public PersistentOutboundQueue(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboundQueueOptions> options,
        OutboundQueueMetrics metrics,
        TimeProvider timeProvider,
        ILogger<PersistentOutboundQueue> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value
            ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Pre-flight dedup probe. The UNIQUE index on IdempotencyKey
        // is the authoritative gate; the probe short-circuits the
        // common hot path (Telegram webhooks retry aggressively, so
        // a duplicate enqueue is a realistic shape) without burning
        // an INSERT round-trip. The concurrent-insert race below is
        // covered by the DbUpdateException catch.
        var existingIdempotent = await db.OutboundMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotencyKey == message.IdempotencyKey, ct)
            .ConfigureAwait(false);
        if (existingIdempotent is not null)
        {
            _logger.LogInformation(
                "OutboundMessage with IdempotencyKey={IdempotencyKey} already enqueued as MessageId={ExistingMessageId}; rejecting duplicate enqueue without creating a second outbox row.",
                message.IdempotencyKey,
                existingIdempotent.MessageId);
            return;
        }

        // Backpressure check. Count the non-terminal outbox depth
        // (Pending + Sending) and dead-letter Low-severity enqueues
        // when the depth exceeds the configured threshold. Per
        // architecture.md §10.4 the guard is severity-asymmetric:
        // Normal/High/Critical messages bypass the gate so the
        // priority queue still drains the urgent traffic during a
        // burst, only Low-severity status updates / acks pay the
        // cost.
        if (message.Severity == MessageSeverity.Low)
        {
            var depth = await db.OutboundMessages
                .AsNoTracking()
                .CountAsync(
                    x => x.Status == OutboundMessageStatus.Pending
                         || x.Status == OutboundMessageStatus.Sending,
                    ct)
                .ConfigureAwait(false);

            // Stage 4.1 iter-2 evaluator item 4 — strictly greater than.
            // The brief says "when queue depth EXCEEDS MaxQueueDepth";
            // `>=` would drop a Low message the moment depth equalled
            // the configured cap (i.e. the queue is full but not yet
            // over capacity). With `>` the cap is the canonical
            // "high-water mark before which all severities are
            // accepted" and the first Low message after depth crosses
            // the cap is the DLQ trigger — matching architecture.md
            // §10.4 verbatim.
            if (depth > _options.MaxQueueDepth)
            {
                var deadLettered = message with
                {
                    Status = OutboundMessageStatus.DeadLettered,
                    ErrorDetail = OutboundQueueOptions.BackpressureDeadLetterReason,
                };
                db.OutboundMessages.Add(deadLettered);
                await SaveDedupingAsync(db, deadLettered, ct).ConfigureAwait(false);

                _metrics.BackpressureDeadLetterCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("severity", message.Severity.ToString()),
                    new KeyValuePair<string, object?>("reason", OutboundQueueOptions.BackpressureDeadLetterReason));

                _logger.LogWarning(
                    "Backpressure dead-letter — queue depth {Depth} > MaxQueueDepth {MaxQueueDepth}; Low-severity MessageId={MessageId} IdempotencyKey={IdempotencyKey} CorrelationId={CorrelationId} written with Status=DeadLettered reason='{Reason}'.",
                    depth,
                    _options.MaxQueueDepth,
                    deadLettered.MessageId,
                    deadLettered.IdempotencyKey,
                    deadLettered.CorrelationId,
                    OutboundQueueOptions.BackpressureDeadLetterReason);
                return;
            }
        }

        // Normal Pending insert. Use the row as-supplied except for
        // MaxAttempts — when the caller did not override the record
        // default (5), bind it to the configured option so a host
        // tuning the global cap via OutboundQueue:MaxRetries does
        // not have to touch every enqueue site.
        var pending = message;
        if (pending.MaxAttempts <= 0)
        {
            pending = pending with { MaxAttempts = _options.MaxRetries };
        }
        db.OutboundMessages.Add(pending);
        await SaveDedupingAsync(db, pending, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OutboundMessage?> DequeueAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var now = _timeProvider.GetUtcNow();

        // Two-step claim loop:
        //   1. SELECT the highest-priority Pending row (or null).
        //   2. UPDATE … WHERE Status='Pending' AND MessageId=@id
        //      → Sending. If 0 rows affected, another worker beat us;
        //      retry from step 1. Bounded by the candidate set being
        //      monotonically draining (the loser observes the rival
        //      row as Sending on the next pass).
        //
        // The composite ix_outbox_status_severity_created index
        // covers the SELECT so the per-iteration cost is O(log n)
        // even under sustained burst. Implemented as a while-loop
        // with a sane max iteration cap so a runaway race cannot
        // pin a worker thread indefinitely (it would yield back to
        // the processor's poll cadence anyway).
        const int maxClaimAttempts = 32;
        for (var attempt = 0; attempt < maxClaimAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var candidate = await db.OutboundMessages
                .AsNoTracking()
                .Where(x => x.Status == OutboundMessageStatus.Pending
                            && (x.NextRetryAt == null || x.NextRetryAt <= now))
                .OrderBy(x => x.Severity)
                .ThenBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (candidate is null)
            {
                return null;
            }

            var dequeuedAt = (DateTimeOffset?)now;
            var rowsAffected = await db.OutboundMessages
                .Where(x => x.MessageId == candidate.MessageId
                            && x.Status == OutboundMessageStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.Status, OutboundMessageStatus.Sending)
                        .SetProperty(x => x.DequeuedAt, dequeuedAt),
                    ct)
                .ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                // Lost the CAS — another worker won the claim. Loop
                // to pick the next candidate.
                continue;
            }

            // Return the freshly-claimed row with the Sending
            // status applied AND the new DequeuedAt timestamp,
            // mirroring the in-memory queue's dequeue contract so
            // the caller (Stage 4.1 iter-2 evaluator item 2) sees a
            // consistent snapshot — the OutboundQueueProcessor can
            // reach for `message.DequeuedAt.Value` without an extra
            // round-trip to re-read the row from the database.
            return candidate with
            {
                Status = OutboundMessageStatus.Sending,
                DequeuedAt = dequeuedAt,
            };
        }

        // Sustained contention or a bug: log and return null so the
        // processor backs off rather than spinning.
        _logger.LogWarning(
            "DequeueAsync gave up after {MaxAttempts} contended claim attempts; returning null so the processor can back off.",
            maxClaimAttempts);
        return null;
    }

    /// <inheritdoc />
    public async Task MarkSentAsync(Guid messageId, long telegramMessageId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var sentAt = (DateTimeOffset?)_timeProvider.GetUtcNow();
        var msgId = (long?)telegramMessageId;

        // Single conditional UPDATE — only transitions Sending → Sent
        // so a rogue caller cannot resurrect a DeadLettered row. The
        // ErrorDetail column is intentionally NOT cleared so a
        // previously-failed-then-recovered row keeps its diagnostic
        // breadcrumb.
        var rowsAffected = await db.OutboundMessages
            .Where(x => x.MessageId == messageId
                        && x.Status == OutboundMessageStatus.Sending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, OutboundMessageStatus.Sent)
                    .SetProperty(x => x.SentAt, sentAt)
                    .SetProperty(x => x.TelegramMessageId, msgId),
                ct)
            .ConfigureAwait(false);

        if (rowsAffected == 0)
        {
            _logger.LogWarning(
                "MarkSentAsync no-op for MessageId={MessageId} — row is missing or no longer in Sending status (likely DeadLettered or already Sent by a sibling worker).",
                messageId);
        }
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(error);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Read-then-conditional-update so we can compute the new
        // AttemptCount + NextRetryAt + Status transition. EF Core's
        // ExecuteUpdateAsync cannot express "set AttemptCount =
        // AttemptCount + 1" portably across providers for the
        // OutboundMessage row shape, so a load+save is the simplest
        // safe shape. The WHERE filter on Status==Sending prevents a
        // late MarkFailed from clobbering a row another worker has
        // already transitioned to DeadLettered.
        var current = await db.OutboundMessages
            .FirstOrDefaultAsync(x => x.MessageId == messageId, ct)
            .ConfigureAwait(false);
        if (current is null)
        {
            _logger.LogWarning(
                "MarkFailedAsync no-op for MessageId={MessageId} — row missing.",
                messageId);
            return;
        }
        if (current.Status != OutboundMessageStatus.Sending)
        {
            _logger.LogWarning(
                "MarkFailedAsync skipping MessageId={MessageId} — Status={Status} is not Sending.",
                messageId,
                current.Status);
            return;
        }

        var nextAttempt = current.AttemptCount + 1;
        var hasBudgetLeft = nextAttempt < current.MaxAttempts;
        var truncated = error.Length > 2048 ? error.Substring(0, 2048) : error;

        // Backoff: 2^n seconds capped at 60. Stage 4.2 swaps this
        // for the canonical RetryPolicy; pinning a sane default here
        // keeps the queue self-contained until that stage lands.
        var delaySeconds = Math.Min(60, 1 << Math.Min(nextAttempt, 6));
        var nextRetryAt = hasBudgetLeft
            ? (DateTimeOffset?)_timeProvider.GetUtcNow().AddSeconds(delaySeconds)
            : null;

        var updated = current with
        {
            Status = hasBudgetLeft ? OutboundMessageStatus.Pending : OutboundMessageStatus.Failed,
            AttemptCount = nextAttempt,
            ErrorDetail = truncated,
            NextRetryAt = nextRetryAt,
        };

        db.Entry(current).State = EntityState.Detached;
        db.OutboundMessages.Update(updated);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeadLetterAsync(Guid messageId, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reason);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Stage 4.1 iter-2 evaluator item 5 — read-then-update so we
        // can preserve the failure reason on ErrorDetail AND bump
        // AttemptCount by one. The prior single-statement
        // ExecuteUpdateAsync that only flipped Status silently
        // dropped both pieces of audit information, so the dead-
        // letter row landed indistinguishable from a row that was
        // dead-lettered for any other cause (no reason, no attempt
        // count delta). Now the terminal transition records
        // *exactly* why the row was given up on, mirroring the
        // canonical backpressure DLQ path in EnqueueAsync that has
        // always stamped ErrorDetail.
        var current = await db.OutboundMessages
            .FirstOrDefaultAsync(x => x.MessageId == messageId, ct)
            .ConfigureAwait(false);
        if (current is null)
        {
            _logger.LogWarning(
                "DeadLetterAsync no-op for MessageId={MessageId} — row missing.",
                messageId);
            return;
        }
        if (current.Status == OutboundMessageStatus.Sent
            || current.Status == OutboundMessageStatus.DeadLettered)
        {
            _logger.LogWarning(
                "DeadLetterAsync no-op for MessageId={MessageId} — Status={Status} is already terminal.",
                messageId,
                current.Status);
            return;
        }

        var truncated = reason.Length > 2048 ? reason.Substring(0, 2048) : reason;
        var updated = current with
        {
            Status = OutboundMessageStatus.DeadLettered,
            AttemptCount = current.AttemptCount + 1,
            ErrorDetail = truncated,
        };

        db.Entry(current).State = EntityState.Detached;
        db.OutboundMessages.Update(updated);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogWarning(
            "DeadLetterAsync MessageId={MessageId} CorrelationId={CorrelationId} AttemptCount={AttemptCount} Reason={Reason}",
            messageId,
            current.CorrelationId,
            updated.AttemptCount,
            truncated);
    }

    /// <summary>
    /// Save the freshly-tracked <paramref name="message"/> and treat a
    /// unique-constraint violation as a successful no-op. The
    /// concurrent-insert race shape: two callers race past the
    /// pre-flight existence probe, both Add() the same
    /// <see cref="OutboundMessage.IdempotencyKey"/>, one
    /// <c>SaveChangesAsync</c> wins, the other surfaces a
    /// <see cref="DbUpdateException"/> whose inner is a provider-
    /// specific UNIQUE-violation exception. Both providers
    /// (SQLite/PostgreSQL/SQL Server) raise distinguishable error
    /// codes; rather than couple to those, we re-query by the
    /// idempotency key — if a row exists, the duplicate was
    /// accepted, otherwise we re-throw to surface the unknown
    /// failure.
    /// </summary>
    private async Task SaveDedupingAsync(MessagingDbContext db, OutboundMessage message, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            db.Entry(message).State = EntityState.Detached;

            var existing = await db.OutboundMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.IdempotencyKey == message.IdempotencyKey, ct)
                .ConfigureAwait(false);

            if (existing is null)
            {
                _logger.LogError(
                    ex,
                    "EnqueueAsync save failed for MessageId={MessageId} IdempotencyKey={IdempotencyKey} and no existing row was found — not a duplicate.",
                    message.MessageId,
                    message.IdempotencyKey);
                throw;
            }

            _logger.LogInformation(
                "EnqueueAsync race resolved — MessageId={MessageId} duplicate of existing MessageId={ExistingMessageId} on IdempotencyKey={IdempotencyKey}; treating as success.",
                message.MessageId,
                existing.MessageId,
                message.IdempotencyKey);
        }
    }
}
