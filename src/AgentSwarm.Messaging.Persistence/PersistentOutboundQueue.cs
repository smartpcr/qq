using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core-backed implementation of <see cref="IOutboundQueue"/>. Provides
/// severity-aware dequeue (Critical → High → Normal → Low, oldest-first
/// within each severity), exponential-backoff retry, UNIQUE idempotency
/// enforcement, and dead-lettering on retry exhaustion per architecture.md
/// Section 3.2 / Section 10.3 and implementation-plan Stage 2.3.
/// </summary>
/// <remarks>
/// Pickup uses an atomic conditional UPDATE (<c>SET Status=Sending WHERE
/// MessageId=@id AND Status IN (Pending, Failed) AND (Pending OR
/// NextRetryAt &lt;= @now)</c>) via <see cref="EntityFrameworkQueryableExtensions"/>'s
/// <c>ExecuteUpdateAsync</c>. The single SQL statement guarantees exactly
/// one concurrent dispatcher transitions a given row, even when multiple
/// dispatchers have read the same candidate id from a stale snapshot
/// (<see cref="DequeueAsync"/> retry loop picks the next candidate when its
/// CAS rejects).
/// </remarks>
public sealed class PersistentOutboundQueue : IOutboundQueue
{
    /// <summary>
    /// Default exponential-backoff base. Retry delay after the <c>n</c>th
    /// failed attempt is <c>BaseBackoff * 2^(n-1)</c> (1s, 2s, 4s, 8s, 16s)
    /// per architecture.md Section 10.3.
    /// </summary>
    public static readonly TimeSpan DefaultBaseBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Cap on the computed retry delay so a large
    /// <see cref="OutboundMessage.MaxAttempts"/> cannot push the next
    /// attempt arbitrarily far into the future.
    /// </summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromMinutes(5);

    // Bounded retry budget for the DequeueAsync claim loop. Each iteration
    // re-reads the highest-priority eligible row and attempts an atomic CAS.
    // Under realistic concurrency (few dispatchers, many candidates) the
    // first iteration almost always wins. A cap keeps a worst-case live-lock
    // (every candidate stolen mid-CAS) bounded.
    private const int MaxClaimRetries = 32;

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
    /// Fast-path AnyAsync check on the
    /// <see cref="OutboundMessage.IdempotencyKey"/> UNIQUE index plus a
    /// catch-as-fallback for the SQLite UNIQUE-constraint violation raised
    /// when a concurrent writer commits the same key between the AnyAsync
    /// snapshot and our SaveChanges. Both paths collapse to a no-op so the
    /// caller never sees an exception for a logically duplicate enqueue.
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
    /// the chosen severity band, atomically transitioning it from
    /// <see cref="OutboundMessageStatus.Pending"/> / eligible-retry
    /// <see cref="OutboundMessageStatus.Failed"/> to
    /// <see cref="OutboundMessageStatus.Sending"/> via a single conditional
    /// UPDATE. When a concurrent dispatcher claims the same candidate first,
    /// the CAS rejects and the loop picks the next eligible row.
    /// </remarks>
    public async Task<OutboundMessage?> DequeueAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxClaimRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var now = _timeProvider.GetUtcNow();
            var candidateId = await SelectEligibleQuery(now)
                .OrderBy(m => (int)m.Severity)
                .ThenBy(m => m.CreatedAt)
                .Select(m => (Guid?)m.MessageId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (candidateId is null)
            {
                return null;
            }

            var claimed = await TryClaimAsync(candidateId.Value, now, ct).ConfigureAwait(false);
            if (claimed is not null)
            {
                return claimed;
            }
        }

        // Live-locked: every candidate was stolen mid-CAS. Returning null
        // is correct -- the caller will poll again on its next tick.
        return null;
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
    /// <paramref name="error"/>, and computes
    /// <see cref="OutboundMessage.NextRetryAt"/> via exponential backoff
    /// (<see cref="DefaultBaseBackoff"/> × 2^(attempt-1), capped at
    /// <see cref="DefaultMaxBackoff"/>). When the post-increment count
    /// reaches <see cref="OutboundMessage.MaxAttempts"/> the row is
    /// transitioned straight to
    /// <see cref="OutboundMessageStatus.DeadLettered"/> and the linked
    /// <see cref="DeadLetterMessage"/> row is inserted in the *same*
    /// SaveChanges. Doing both writes atomically closes a re-dispatch race
    /// where a concurrent worker, observing the intermediate
    /// <c>Status=Failed, NextRetryAt=null</c> snapshot, would treat the
    /// exhausted message as immediately eligible via
    /// <see cref="SelectEligibleQuery"/>.
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
                Status = OutboundMessageStatus.DeadLettered,
                AttemptCount = newAttemptCount,
                ErrorDetail = error,
                NextRetryAt = null,
            };
            _context.Entry(existing).CurrentValues.SetValues(exhausted);

            // Persist the status transition and the dead-letter insert in a
            // single unit of work, deferring duplicate suppression to the
            // UNIQUE(OriginalMessageId) constraint rather than a TOCTOU
            // pre-check. A concurrent operator-initiated DeadLetterAsync
            // racing this exhaustion path will collide on the unique index;
            // the catch handler detaches our duplicate insert and re-saves
            // the status transition alone.
            await PersistDeadLetterTransitionAsync(
                exhausted,
                BuildErrorReason(exhausted),
                exhausted.AttemptCount,
                _timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
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
    /// <see cref="OutboundMessageStatus.DeadLettered"/> (the outbound row is
    /// retained for operator-side requeue) and creates the linked
    /// <see cref="DeadLetterMessage"/> via the
    /// <c>DeadLetterMessage.OriginalMessageId → OutboundMessage.MessageId</c>
    /// foreign key. The <c>UNIQUE(OriginalMessageId)</c> index enforces the
    /// architecture.md Section 3.2 <c>1--1</c> relationship; a redundant
    /// dead-letter call re-asserts the status without adding a second row.
    /// </remarks>
    public async Task DeadLetterAsync(Guid messageId, CancellationToken ct)
    {
        var existing = await LoadTrackedAsync(messageId, ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        if (existing.Status != OutboundMessageStatus.DeadLettered)
        {
            var dead = existing with
            {
                Status = OutboundMessageStatus.DeadLettered,
                NextRetryAt = null,
            };
            _context.Entry(existing).CurrentValues.SetValues(dead);
        }

        // Defer duplicate suppression to the UNIQUE(OriginalMessageId)
        // index instead of a TOCTOU AnyAsync pre-check: under concurrent
        // DeadLetter / MarkFailed-exhaustion calls the read-then-write
        // window would allow both callers to insert. The catch-fallback
        // in PersistDeadLetterTransitionAsync collapses the duplicate to
        // a single row by detaching and re-saving the status transition.
        await PersistDeadLetterTransitionAsync(
            existing,
            BuildErrorReason(existing),
            existing.AttemptCount,
            now,
            ct).ConfigureAwait(false);
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
    /// <see cref="OutboundMessage.CreatedAt"/>. Each candidate is claimed
    /// via the same atomic CAS used by <see cref="DequeueAsync"/>, so two
    /// dispatchers that select overlapping batches will each only return
    /// the rows they successfully transitioned; collisions are dropped.
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

        var candidateIds = await SelectEligibleQuery(now)
            .Where(m => m.Severity == severity)
            .OrderBy(m => m.CreatedAt)
            .Take(maxCount)
            .Select(m => m.MessageId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0)
        {
            return Array.Empty<OutboundMessage>();
        }

        var claimed = new List<OutboundMessage>(candidateIds.Count);
        foreach (var id in candidateIds)
        {
            var msg = await TryClaimAsync(id, now, ct).ConfigureAwait(false);
            if (msg is not null)
            {
                claimed.Add(msg);
            }
        }

        return claimed;
    }

    /// <summary>
    /// Atomic conditional pickup: transitions a single row to
    /// <see cref="OutboundMessageStatus.Sending"/> via a single UPDATE whose
    /// WHERE clause re-asserts the eligibility predicate. Returns the
    /// updated message when the UPDATE matched (1 row affected), or
    /// <see langword="null"/> when another worker won the race (0 rows
    /// affected). Exposed <c>internal</c> for direct concurrency tests that
    /// orchestrate interleaved claim attempts; production callers go through
    /// <see cref="DequeueAsync"/> / <see cref="DequeueBatchAsync"/>.
    /// </summary>
    internal async Task<OutboundMessage?> TryClaimAsync(
        Guid messageId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rowsAffected = await _context.OutboundMessages
            .Where(m =>
                m.MessageId == messageId
                && (m.Status == OutboundMessageStatus.Pending
                    || (m.Status == OutboundMessageStatus.Failed
                        && (m.NextRetryAt == null || m.NextRetryAt <= now))))
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.Status, OutboundMessageStatus.Sending),
                ct)
            .ConfigureAwait(false);

        if (rowsAffected == 0)
        {
            return null;
        }

        return await _context.OutboundMessages
            .AsNoTracking()
            .FirstAsync(m => m.MessageId == messageId, ct)
            .ConfigureAwait(false);
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

    /// <summary>
    /// Persists the Sending/Failed → DeadLettered status transition (already
    /// applied to the tracked <paramref name="exhausted"/> entity by the
    /// caller) and inserts the linked <see cref="DeadLetterMessage"/> in a
    /// single unit of work. Concurrent callers (e.g. a worker hitting the
    /// MarkFailed exhaustion path while an operator separately invokes
    /// DeadLetterAsync) collide on
    /// <c>IX_DeadLetterMessages_OriginalMessageId_Unique</c>; the catch
    /// handler detaches our duplicate insert and re-saves the status
    /// transition alone so the row remains in DeadLettered state and the
    /// caller never observes the UNIQUE-violation exception.
    /// </summary>
    private async Task PersistDeadLetterTransitionAsync(
        OutboundMessage exhausted,
        string errorReason,
        int attemptCount,
        DateTimeOffset failedAt,
        CancellationToken ct)
    {
        var deadLetter = new DeadLetterMessage
        {
            OriginalMessageId = exhausted.MessageId,
            ChatId = exhausted.ChatId,
            Payload = exhausted.Payload,
            ErrorReason = errorReason,
            FailedAt = failedAt,
            AttemptCount = attemptCount,
        };

        _context.DeadLetterMessages.Add(deadLetter);

        try
        {
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _context.Entry(deadLetter).State = EntityState.Detached;
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private IQueryable<OutboundMessage> SelectEligibleQuery(DateTimeOffset now)
    {
        // OutboundMessage timestamp columns use the UTC-ticks INTEGER
        // converter so the EF Core 8 SQLite provider translates the
        // NextRetryAt <= now comparison to a native INTEGER predicate.
        return _context.OutboundMessages.AsNoTracking().Where(m =>
            m.Status == OutboundMessageStatus.Pending
            || (m.Status == OutboundMessageStatus.Failed
                && (m.NextRetryAt == null || m.NextRetryAt <= now)));
    }

    private TimeSpan ComputeBackoff(int attemptCount)
    {
        // Post-increment attemptCount is >= 1; schedule is 1s, 2s, 4s, ...
        // capped at MaxBackoff.
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
        // SQLITE_CONSTRAINT = 19, SQLITE_CONSTRAINT_UNIQUE = 2067.
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
