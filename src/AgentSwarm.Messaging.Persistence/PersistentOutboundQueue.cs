using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core-backed implementation of <see cref="IOutboundQueue"/> persisting
/// outbound messages in the shared <see cref="MessagingDbContext"/> store.
/// Provides severity-aware dequeue (Critical → High → Normal → Low,
/// oldest-first within each severity), exponential-backoff retry, UNIQUE
/// idempotency enforcement, and dead-lettering on retry exhaustion per
/// architecture.md Section 3.2 / Section 10.3 and implementation-plan
/// Stage 2.3.
/// </summary>
/// <remarks>
/// <para>
/// All mutating operations execute against a single <see cref="MessagingDbContext"/>
/// instance and are intended to run with one writer-per-context. Concurrent
/// dispatchers should each own a scoped context; SQLite serializes writes at
/// the database level so the transactional pickup in
/// <see cref="DequeueAsync"/> / <see cref="DequeueBatchAsync"/> safely
/// transitions a row from <see cref="OutboundMessageStatus.Pending"/> /
/// <see cref="OutboundMessageStatus.Failed"/> to
/// <see cref="OutboundMessageStatus.Sending"/> without two dispatchers
/// claiming the same row.
/// </para>
/// <para>
/// The <see cref="OutboundMessage"/> record carries init-only positional
/// properties. We honour immutability of the in-memory object by composing a
/// new record via <c>with</c> expressions and projecting the updated values
/// into the tracked entity's change tracker via
/// <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues.SetValues(object)"/>.
/// EF Core emits the resulting <c>UPDATE</c> against the change set.
/// </para>
/// </remarks>
public sealed class PersistentOutboundQueue : IOutboundQueue
{
    /// <summary>
    /// Default exponential-backoff base. The retry delay after the
    /// <c>n</c>th failed attempt is <c>BaseBackoff * 2^(n-1)</c>, so the
    /// pinned schedule (per architecture.md Section 10.3) is
    /// 1s, 2s, 4s, 8s, 16s before exhaustion at <c>MaxAttempts</c>.
    /// </summary>
    public static readonly TimeSpan DefaultBaseBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Cap on the computed retry delay. Without a cap, a high
    /// <see cref="OutboundMessage.MaxAttempts"/> would push the next attempt
    /// arbitrarily far into the future; the cap keeps retries operator-
    /// reasonable on long-tail failures.
    /// </summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromMinutes(5);

    private readonly MessagingDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _maxBackoff;

    /// <summary>
    /// Creates a new queue against <paramref name="context"/>.
    /// </summary>
    /// <param name="context">Shared EF context for the messenger schema.</param>
    /// <param name="timeProvider">
    /// Clock used for <see cref="OutboundMessage.NextRetryAt"/> and
    /// <see cref="OutboundMessage.SentAt"/>. Defaults to
    /// <see cref="TimeProvider.System"/>; tests inject a fake provider so
    /// backoff schedules become deterministic.
    /// </param>
    /// <param name="baseBackoff">
    /// Overrides <see cref="DefaultBaseBackoff"/>. Useful in tests that want
    /// to verify the exponential formula without sleeping.
    /// </param>
    /// <param name="maxBackoff">
    /// Overrides <see cref="DefaultMaxBackoff"/>.
    /// </param>
    public PersistentOutboundQueue(
        MessagingDbContext context,
        TimeProvider? timeProvider = null,
        TimeSpan? baseBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _baseBackoff = baseBackoff ?? DefaultBaseBackoff;
        _maxBackoff = maxBackoff ?? DefaultMaxBackoff;

        if (_baseBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseBackoff),
                _baseBackoff, "Base backoff must be greater than zero.");
        }

        if (_maxBackoff < _baseBackoff)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBackoff),
                _maxBackoff, "Max backoff must be at least baseBackoff.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Performs a fast-path duplicate check against the
    /// <see cref="OutboundMessage.IdempotencyKey"/> UNIQUE index, then falls
    /// back to swallowing the SQLite UNIQUE-constraint violation if a
    /// concurrent writer beat us to the INSERT. Either way the call is a
    /// no-op when the key already exists, honouring the contract on
    /// <see cref="IOutboundQueue.EnqueueAsync"/>.
    /// </remarks>
    public async Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var exists = await _context.OutboundMessages
            .AsNoTracking()
            .AnyAsync(m => m.IdempotencyKey == message.IdempotencyKey, ct)
            .ConfigureAwait(false);

        if (exists)
        {
            return;
        }

        _context.OutboundMessages.Add(message);

        try
        {
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _context.Entry(message).State = EntityState.Detached;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the highest-severity eligible message (Critical first, Low
    /// last) with the earliest <see cref="OutboundMessage.CreatedAt"/> within
    /// the chosen severity band. A message is eligible when it is
    /// <see cref="OutboundMessageStatus.Pending"/>, or when it is
    /// <see cref="OutboundMessageStatus.Failed"/> with
    /// <see cref="OutboundMessage.NextRetryAt"/> at or before the current
    /// clock. The selected row is transitioned to
    /// <see cref="OutboundMessageStatus.Sending"/> in the same SaveChanges so
    /// concurrent dispatchers do not pick it up twice.
    /// </remarks>
    public async Task<OutboundMessage?> DequeueAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        // The IX_OutboundMessages_Status_Severity_NextRetryAt composite
        // index covers this scan; OutboundMessage stores its timestamp
        // columns as UTC-ticks INTEGER (see
        // UtcDateTimeOffsetTicksConverter) so the EF Core 8 SQLite
        // provider can translate the NextRetryAt <= now comparison.
        var existing = await SelectEligibleQuery(now)
            .OrderBy(m => (int)m.Severity)
            .ThenBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return null;
        }

        return await ClaimForSendingAsync(existing, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkSentAsync(Guid messageId, long platformMessageId, CancellationToken ct)
    {
        var existing = await LoadTrackedAsync(messageId, ct).ConfigureAwait(false);

        var updated = existing with
        {
            Status = OutboundMessageStatus.Sent,
            PlatformMessageId = platformMessageId,
            SentAt = _timeProvider.GetUtcNow(),
            NextRetryAt = null,
            ErrorDetail = null,
        };

        _context.Entry(existing).CurrentValues.SetValues(updated);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Increments <see cref="OutboundMessage.AttemptCount"/>, persists
    /// <paramref name="error"/> in <see cref="OutboundMessage.ErrorDetail"/>,
    /// and computes <see cref="OutboundMessage.NextRetryAt"/> via the
    /// exponential schedule (<see cref="DefaultBaseBackoff"/> ×
    /// 2^(attempt-1), capped at <see cref="DefaultMaxBackoff"/>). When the
    /// post-increment attempt count reaches
    /// <see cref="OutboundMessage.MaxAttempts"/>, the message is transitioned
    /// to <see cref="OutboundMessageStatus.DeadLettered"/> via
    /// <see cref="DeadLetterAsync"/> and a linked
    /// <see cref="DeadLetterMessage"/> row is created.
    /// </remarks>
    public async Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(error);

        var existing = await LoadTrackedAsync(messageId, ct).ConfigureAwait(false);
        var newAttemptCount = existing.AttemptCount + 1;

        if (newAttemptCount >= existing.MaxAttempts)
        {
            var exhausted = existing with
            {
                Status = OutboundMessageStatus.Failed,
                AttemptCount = newAttemptCount,
                ErrorDetail = error,
                NextRetryAt = null,
            };
            _context.Entry(existing).CurrentValues.SetValues(exhausted);
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);

            await DeadLetterAsync(messageId, ct).ConfigureAwait(false);
            return;
        }

        var nextRetryAt = _timeProvider.GetUtcNow() + ComputeBackoff(newAttemptCount);
        var failed = existing with
        {
            Status = OutboundMessageStatus.Failed,
            AttemptCount = newAttemptCount,
            ErrorDetail = error,
            NextRetryAt = nextRetryAt,
        };

        _context.Entry(existing).CurrentValues.SetValues(failed);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Transitions <paramref name="messageId"/> to
    /// <see cref="OutboundMessageStatus.DeadLettered"/> (the OutboundMessage
    /// row is retained for operator-side requeue) and creates the linked
    /// <see cref="DeadLetterMessage"/> via the
    /// <c>DeadLetterMessage.OriginalMessageId → OutboundMessage.MessageId</c>
    /// foreign key. The dead-letter row carries the chat id, payload, last
    /// error reason, attempt count and failure timestamp so operator triage
    /// does not need to join back to the outbound table. The
    /// <c>UNIQUE(OriginalMessageId)</c> index enforces the
    /// <c>1--1</c> architecture.md Section 3.2 relationship; a redundant
    /// dead-letter call simply re-asserts the dead-lettered status without
    /// adding a second row.
    /// </remarks>
    public async Task DeadLetterAsync(Guid messageId, CancellationToken ct)
    {
        var existing = await LoadTrackedAsync(messageId, ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        var alreadyDeadLettered = await _context.DeadLetterMessages
            .AsNoTracking()
            .AnyAsync(d => d.OriginalMessageId == messageId, ct)
            .ConfigureAwait(false);

        if (existing.Status != OutboundMessageStatus.DeadLettered)
        {
            var dead = existing with
            {
                Status = OutboundMessageStatus.DeadLettered,
                NextRetryAt = null,
            };
            _context.Entry(existing).CurrentValues.SetValues(dead);
        }

        if (!alreadyDeadLettered)
        {
            _context.DeadLetterMessages.Add(new DeadLetterMessage
            {
                OriginalMessageId = existing.MessageId,
                ChatId = existing.ChatId,
                Payload = existing.Payload,
                ErrorReason = BuildErrorReason(existing),
                FailedAt = now,
                AttemptCount = existing.AttemptCount,
            });
        }

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountPendingAsync(MessageSeverity severity, CancellationToken ct)
    {
        return await _context.OutboundMessages
            .CountAsync(
                m => m.Status == OutboundMessageStatus.Pending && m.Severity == severity,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Drains up to <paramref name="maxCount"/> eligible messages of
    /// <paramref name="severity"/>, oldest-first by
    /// <see cref="OutboundMessage.CreatedAt"/>. The same eligibility rule as
    /// <see cref="DequeueAsync"/> applies (Pending or Failed with elapsed
    /// retry). All returned rows are atomically transitioned to
    /// <see cref="OutboundMessageStatus.Sending"/> in a single SaveChanges
    /// so concurrent dispatchers cannot dequeue them again.
    /// </remarks>
    public async Task<IReadOnlyList<OutboundMessage>> DequeueBatchAsync(
        MessageSeverity severity,
        int maxCount,
        CancellationToken ct)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<OutboundMessage>();
        }

        var now = _timeProvider.GetUtcNow();

        // Severity is fixed by the caller, so the priority dimension drops
        // out of the ORDER BY; CreatedAt alone gives oldest-first within
        // the band. Eligibility is expressible in SQL because the
        // timestamp columns use the UTC-ticks INTEGER converter.
        var eligible = await SelectEligibleQuery(now)
            .Where(m => m.Severity == severity)
            .OrderBy(m => m.CreatedAt)
            .Take(maxCount)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (eligible.Count == 0)
        {
            return Array.Empty<OutboundMessage>();
        }

        var claimed = new List<OutboundMessage>(eligible.Count);
        foreach (var candidate in eligible)
        {
            var updated = candidate with { Status = OutboundMessageStatus.Sending };
            _context.Entry(candidate).CurrentValues.SetValues(updated);
            claimed.Add(updated);
        }

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        return claimed;
    }

    private async Task<OutboundMessage> LoadTrackedAsync(Guid messageId, CancellationToken ct)
    {
        var existing = await _context.OutboundMessages
            .FirstOrDefaultAsync(m => m.MessageId == messageId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            throw new InvalidOperationException(
                $"OutboundMessage '{messageId}' not found in the outbox.");
        }

        return existing;
    }

    private async Task<OutboundMessage> ClaimForSendingAsync(
        OutboundMessage existing,
        CancellationToken ct)
    {
        var updated = existing with { Status = OutboundMessageStatus.Sending };
        _context.Entry(existing).CurrentValues.SetValues(updated);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        return updated;
    }

    private IQueryable<OutboundMessage> SelectEligibleQuery(DateTimeOffset now)
    {
        // Pending rows are always eligible; Failed rows become eligible
        // once their NextRetryAt is at or before "now" (a null
        // NextRetryAt -- e.g. a row whose retry schedule was cleared --
        // is treated as immediately eligible to match the IOutboundQueue
        // contract docstring). OutboundMessage timestamp columns use the
        // UTC-ticks INTEGER converter so the EF Core 8 SQLite provider
        // translates this comparison without falling back to the client.
        return _context.OutboundMessages.Where(m =>
            m.Status == OutboundMessageStatus.Pending
            || (m.Status == OutboundMessageStatus.Failed
                && (m.NextRetryAt == null || m.NextRetryAt <= now)));
    }

    private TimeSpan ComputeBackoff(int attemptCount)
    {
        // attemptCount is the post-increment value (>= 1 after MarkFailedAsync).
        // Schedule: 1s, 2s, 4s, 8s, 16s ... capped at MaxBackoff.
        var exponent = Math.Max(0, attemptCount - 1);
        var multiplier = Math.Pow(2, exponent);
        var ticks = _baseBackoff.Ticks * multiplier;

        if (double.IsInfinity(ticks) || ticks > _maxBackoff.Ticks)
        {
            return _maxBackoff;
        }

        return TimeSpan.FromTicks((long)ticks);
    }

    private static string BuildErrorReason(OutboundMessage exhausted)
    {
        var summary = string.Format(
            CultureInfo.InvariantCulture,
            "Dead-lettered after {0} attempt(s) (MaxAttempts={1}).",
            exhausted.AttemptCount,
            exhausted.MaxAttempts);

        return string.IsNullOrEmpty(exhausted.ErrorDetail)
            ? summary
            : summary + " Final error: " + exhausted.ErrorDetail;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // SQLite raises SQLITE_CONSTRAINT (19) and its UNIQUE sub-code
        // (SQLITE_CONSTRAINT_UNIQUE = 2067) on a UNIQUE-index conflict.
        // Either is sufficient evidence that the row already exists; we
        // collapse both into the "duplicate enqueue → no-op" path.
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqliteException sqlite
                && (sqlite.SqliteErrorCode == 19 || sqlite.SqliteExtendedErrorCode == 2067))
            {
                return true;
            }
        }

        return false;
    }
}
