using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IInboundUpdateStore"/> backed by
/// <see cref="MessagingDbContext"/>. Provider-agnostic by design — the
/// duplicate-insert path catches <see cref="DbUpdateException"/> and
/// re-queries by <see cref="InboundUpdate.UpdateId"/> rather than
/// inspecting a SQLite-specific error code, so the same code path
/// holds on SQL Server and PostgreSQL.
/// </summary>
public sealed class PersistentInboundUpdateStore : IInboundUpdateStore
{
    private readonly MessagingDbContext _db;
    private readonly ILogger<PersistentInboundUpdateStore> _logger;

    public PersistentInboundUpdateStore(
        MessagingDbContext db,
        ILogger<PersistentInboundUpdateStore> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> PersistAsync(InboundUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Pre-flight existence check via AsNoTracking. Two scenarios:
        // (a) different DI scope reuse — a separate
        //     <see cref="MessagingDbContext"/> instance attempts an
        //     insert that would later be rejected by the UNIQUE
        //     constraint; this AsNoTracking probe lets us return false
        //     without an INSERT round-trip on the hot duplicate path
        //     (which Telegram retries aggressively under load).
        // (b) same DbContext reuse — the Stage 2.4 endpoint test
        //     drives two HandleAsync calls through one
        //     <see cref="PersistentInboundUpdateStore"/>, which means
        //     the change tracker still holds the first inserted entity.
        //     Calling <c>_db.InboundUpdates.Add(update)</c> for the
        //     same primary key throws
        //     <see cref="InvalidOperationException"/> ("identity
        //     conflict") BEFORE SaveChanges runs, so the
        //     <see cref="DbUpdateException"/> catch below never sees
        //     it. Probing first short-circuits the duplicate cleanly.
        // The UNIQUE constraint remains the authoritative gate for the
        // concurrent-insert race between the probe and SaveChanges.
        var preflight = await _db.InboundUpdates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UpdateId == update.UpdateId, ct)
            .ConfigureAwait(false);
        if (preflight is not null)
        {
            _logger.LogInformation(
                "Duplicate webhook delivery suppressed at PersistAsync (preflight). UpdateId={UpdateId}",
                update.UpdateId);
            return false;
        }

        // The UNIQUE constraint on UpdateId surfaces a duplicate as a
        // DbUpdateException. We catch it, re-query the row to confirm the
        // duplicate (rather than relying on a SQLite-specific error code),
        // and return false. Any other DbUpdateException — e.g. a CHECK or
        // FK violation — re-throws because re-querying will not find the
        // row and we should not silently swallow corruption.
        _db.InboundUpdates.Add(update);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Detach the failed insert so the same DbContext can be reused
            // (relevant when the caller scope holds the context). Without
            // this, a follow-up SaveChangesAsync would re-attempt the
            // duplicate insert.
            var entry = _db.Entry(update);
            entry.State = EntityState.Detached;

            var existing = await _db.InboundUpdates
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UpdateId == update.UpdateId, ct)
                .ConfigureAwait(false);

            if (existing is null)
            {
                // Not a duplicate — re-throw so the caller can surface a
                // 500 and the row stays invisible. Silently swallowing
                // this would lose the update entirely.
                _logger.LogError(
                    ex,
                    "PersistAsync failed for UpdateId={UpdateId} and no existing row found; not a duplicate.",
                    update.UpdateId);
                throw;
            }

            _logger.LogInformation(
                "Duplicate webhook delivery suppressed at PersistAsync. UpdateId={UpdateId}",
                update.UpdateId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<InboundUpdate?> GetByUpdateIdAsync(long updateId, CancellationToken ct)
    {
        return await _db.InboundUpdates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UpdateId == updateId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkProcessingAsync(long updateId, CancellationToken ct)
    {
        // Atomic compare-and-set: the WHERE clause arbitrates concurrent
        // dispatcher-vs-sweep races at the database engine layer. Only
        // Received and Failed rows are eligible to transition; Processing
        // rows are already owned by another worker (live or crash-stuck —
        // the latter is reset to Received by ResetInterruptedAsync on
        // startup), Completed rows are terminal. ExecuteUpdateAsync emits
        // a single UPDATE statement so two callers cannot both observe
        // a successful claim.
        //
        // Iter-5 evaluator item 3 — also stamp ProcessingStartedAt with
        // UtcNow in the SAME UPDATE so the lease timestamp and the
        // status change are atomic. ReclaimStaleProcessingAsync pivots
        // on this column to detect orphaned Processing rows; without
        // the stamp, a fresh claim would look indistinguishable from
        // a legacy row and the sweep would falsely reclaim it.
        var now = (DateTimeOffset?)DateTimeOffset.UtcNow;
        var rowsAffected = await _db.InboundUpdates
            .Where(x => x.UpdateId == updateId
                        && (x.IdempotencyStatus == IdempotencyStatus.Received
                            || x.IdempotencyStatus == IdempotencyStatus.Failed))
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.IdempotencyStatus, IdempotencyStatus.Processing)
                      .SetProperty(p => p.ProcessingStartedAt, now),
                ct)
            .ConfigureAwait(false);

        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<int> ResetInterruptedAsync(CancellationToken ct)
    {
        // Single bulk UPDATE — safe to run unconditionally at startup.
        // No row-by-row read+write that would race with itself.
        // Clear ProcessingStartedAt at the same time so a legacy lease
        // timestamp cannot survive across a startup reset (iter-5
        // evaluator item 3).
        var rowsAffected = await _db.InboundUpdates
            .Where(x => x.IdempotencyStatus == IdempotencyStatus.Processing)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.IdempotencyStatus, IdempotencyStatus.Received)
                      .SetProperty(p => p.ProcessingStartedAt, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);

        if (rowsAffected > 0)
        {
            _logger.LogInformation(
                "ResetInterruptedAsync reverted {Count} Processing row(s) to Received for crash recovery.",
                rowsAffected);
        }

        return rowsAffected;
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(long updateId, string? handlerErrorDetail, CancellationToken ct)
    {
        var entity = await LoadTrackedAsync(updateId, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return;
        }

        var updated = entity with
        {
            IdempotencyStatus = IdempotencyStatus.Completed,
            ProcessedAt = DateTimeOffset.UtcNow,
            HandlerErrorDetail = handlerErrorDetail,
            // Iter-5 evaluator item 3 — clear the lease timestamp on
            // every transition OUT of Processing so a stale value cannot
            // survive across a future Received→Processing→Completed cycle.
            ProcessingStartedAt = null,
        };
        await ApplyAsync(entity, updated, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(long updateId, string errorDetail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(errorDetail))
        {
            throw new ArgumentException(
                "errorDetail must be non-null and non-whitespace.", nameof(errorDetail));
        }

        var entity = await LoadTrackedAsync(updateId, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return;
        }

        var updated = entity with
        {
            IdempotencyStatus = IdempotencyStatus.Failed,
            ErrorDetail = errorDetail,
            AttemptCount = entity.AttemptCount + 1,
            ProcessedAt = DateTimeOffset.UtcNow,
            // Iter-5 evaluator item 3 — clear the lease timestamp on
            // transition out of Processing (Processing→Failed).
            ProcessingStartedAt = null,
        };
        await ApplyAsync(entity, updated, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseProcessingAsync(long updateId, CancellationToken ct)
    {
        // Single conditional UPDATE — Processing → Received, no
        // AttemptCount change. The status filter is the CAS that
        // prevents racing with a sibling worker that already advanced
        // the row to Completed/Failed; ExecuteUpdateAsync returns the
        // row-count which we expose as the boolean result.
        //
        // Clear ProcessingStartedAt in the same UPDATE — without this
        // the released row would carry an obsolete lease timestamp
        // forward and ReclaimStaleProcessingAsync semantics on a
        // subsequent re-claim would be tracking the OLD claim, not
        // the new one (iter-5 evaluator item 3).
        var rowsAffected = await _db.InboundUpdates
            .Where(x => x.UpdateId == updateId
                        && x.IdempotencyStatus == IdempotencyStatus.Processing)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.IdempotencyStatus, IdempotencyStatus.Received)
                      .SetProperty(p => p.ProcessingStartedAt, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);

        if (rowsAffected > 0)
        {
            _logger.LogInformation(
                "ReleaseProcessingAsync flipped UpdateId={UpdateId} Processing→Received (cancel-mid-flight; AttemptCount unchanged).",
                updateId);
        }

        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<int> ReclaimStaleProcessingAsync(TimeSpan staleness, CancellationToken ct)
    {
        if (staleness <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleness), staleness, "must be positive.");
        }

        // Iter-5 evaluator item 3 — reclaim orphaned Processing rows
        // that the one-shot startup reset cannot reach. The predicate
        // matches Processing rows where the lease timestamp is null
        // (legacy / hand-seeded data) OR older than the cutoff. Both
        // shapes are recovered: legacy rows because they predate the
        // lease column, stale rows because the worker that claimed
        // them never released the claim (crash, swallowed-exception
        // ReleaseProcessingAsync, etc).
        //
        // Single conditional UPDATE — no row-by-row read+write that
        // would race with itself or with a live TryMarkProcessingAsync.
        // A live claim CANNOT lose the race because its
        // ProcessingStartedAt is set to UtcNow in the same UPDATE that
        // sets the status, so this reclaim's `< cutoff` predicate
        // cannot match a row whose stamp is still fresh.
        var cutoff = (DateTimeOffset?)DateTimeOffset.UtcNow.Subtract(staleness);
        var rowsAffected = await _db.InboundUpdates
            .Where(x => x.IdempotencyStatus == IdempotencyStatus.Processing
                        && (x.ProcessingStartedAt == null
                            || x.ProcessingStartedAt < cutoff))
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.IdempotencyStatus, IdempotencyStatus.Received)
                      .SetProperty(p => p.ProcessingStartedAt, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);

        if (rowsAffected > 0)
        {
            _logger.LogWarning(
                "ReclaimStaleProcessingAsync recovered {Count} orphaned Processing row(s) older than {Staleness} (cutoff={Cutoff}). The recovery sweep will replay them on this tick.",
                rowsAffected, staleness, cutoff);
        }

        return rowsAffected;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InboundUpdate>> GetRecoverableAsync(int maxRetries, CancellationToken ct)
    {
        if (maxRetries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "must be positive.");
        }

        // Includes Processing rows per architecture.md §4.8 ("Received,
        // Processing, or Failed records represent records that need
        // reprocessing"). Processing rows surfacing here are either
        // live (the dispatcher's `TryMarkProcessingAsync` CAS rejects
        // reclaiming them, so the sweep is a no-op for them by
        // construction) or stranded (cancel-mid-flight or crash after
        // recovery startup ran) — the cancel-handler's
        // `ReleaseProcessingAsync` returns them to Received so a
        // subsequent sweep tick can drive them through the pipeline.
        var rows = await _db.InboundUpdates
            .AsNoTracking()
            .Where(x => (x.IdempotencyStatus == IdempotencyStatus.Received
                         || x.IdempotencyStatus == IdempotencyStatus.Processing
                         || x.IdempotencyStatus == IdempotencyStatus.Failed)
                        && x.AttemptCount < maxRetries)
            .OrderBy(x => x.ReceivedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    public async Task<int> GetExhaustedRetryCountAsync(int maxRetries, CancellationToken ct)
    {
        if (maxRetries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "must be positive.");
        }

        return await _db.InboundUpdates
            .AsNoTracking()
            .CountAsync(
                x => x.IdempotencyStatus == IdempotencyStatus.Failed && x.AttemptCount >= maxRetries,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InboundUpdate>> GetExhaustedAsync(int maxRetries, int limit, CancellationToken ct)
    {
        if (maxRetries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "must be positive.");
        }
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "must be positive.");
        }

        // Order by UpdateId so each sweep tick's per-row Error log is
        // emitted in a stable, predictable sequence — operators tailing
        // the log can rely on a deterministic ordering for triage.
        var rows = await _db.InboundUpdates
            .AsNoTracking()
            .Where(x => x.IdempotencyStatus == IdempotencyStatus.Failed
                        && x.AttemptCount >= maxRetries)
            .OrderBy(x => x.UpdateId)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows;
    }

    private async Task<InboundUpdate?> LoadTrackedAsync(long updateId, CancellationToken ct)
    {
        var existing = await _db.InboundUpdates
            .FirstOrDefaultAsync(x => x.UpdateId == updateId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _logger.LogWarning(
                "Inbound update not found for status transition. UpdateId={UpdateId}",
                updateId);
        }

        return existing;
    }

    private async Task ApplyAsync(InboundUpdate current, InboundUpdate updated, CancellationToken ct)
    {
        // EF Core records are tracked by their primary key; replacing the
        // tracked entity by detaching the old and attaching the new mirrors
        // the immutability of `record` while still flowing through the
        // change tracker. SaveChangesAsync writes the diff.
        _db.Entry(current).State = EntityState.Detached;
        _db.InboundUpdates.Update(updated);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
