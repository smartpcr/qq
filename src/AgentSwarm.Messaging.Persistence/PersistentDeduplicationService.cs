// -----------------------------------------------------------------------
// <copyright file="PersistentDeduplicationService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 4.3 — EF Core-backed <see cref="IDeduplicationService"/> with a
/// sliding-window <c>processed_events</c> table. Replaces the Stage 2.2
/// in-memory <c>InMemoryDeduplicationService</c> stub for production
/// deployments. The companion <see cref="DeduplicationCleanupService"/>
/// purges entries older than
/// <see cref="DeduplicationOptions.EntryTimeToLive"/> on the
/// <see cref="DeduplicationOptions.PurgeInterval"/> cadence so the
/// table stays bounded under burst load.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime + scope.</b> Registered as a singleton; the
/// implementation uses
/// <see cref="IServiceScopeFactory"/> to create a fresh scope per call
/// and resolve the scoped <see cref="MessagingDbContext"/>, bridging
/// the singleton inbound pipeline to the scoped EF context without
/// violating the captive-dependency rule. Matches the pattern used by
/// <see cref="PersistentOutboundDeadLetterStore"/>,
/// <see cref="PersistentOutboundMessageIdIndex"/>,
/// <see cref="PersistentAuditLogger"/>, and
/// <see cref="PersistentTaskOversightRepository"/>.
/// </para>
/// <para>
/// <b>Two-phase row state.</b> A <see cref="ProcessedEvent"/> row's
/// <see cref="ProcessedEvent.ProcessedAt"/> column distinguishes the
/// two lifecycle phases mandated by
/// <see cref="IDeduplicationService"/>:
/// <list type="bullet">
///   <item><description>
///   <c>ProcessedAt IS NULL</c> — the row was claimed by
///   <see cref="TryReserveAsync"/> but
///   <see cref="MarkProcessedAsync"/> has not yet promoted it. The
///   row is still a hard duplicate gate (a second
///   <see cref="TryReserveAsync"/> for the same event id returns
///   <c>false</c> because the PK is taken), but a caught-handler-
///   exception path can call <see cref="ReleaseReservationAsync"/>
///   to delete the row and let a live re-delivery proceed
///   (per the Stage 2.2 brief Scenario 4 invariant).
///   </description></item>
///   <item><description>
///   <c>ProcessedAt IS NOT NULL</c> — the row is the sticky
///   "fully processed" marker. <see cref="ReleaseReservationAsync"/>
///   is a no-op in this state; the row only leaves the table when
///   <see cref="DeduplicationCleanupService"/> evicts it after the
///   TTL elapses.
///   </description></item>
/// </list>
/// The contract matches the in-memory
/// <c>InMemoryDeduplicationService</c> stub it replaces, so wiring
/// this implementation in <c>AddMessagingPersistence</c> is
/// behaviour-preserving for the live inbound pipeline.
/// </para>
/// <para>
/// <b>Atomic concurrency.</b>
/// <see cref="TryReserveAsync"/> is a single INSERT against the
/// <c>EventId</c> primary key. Concurrent callers for the same id
/// all attempt the insert; the database's UNIQUE-on-PK constraint
/// guarantees only one succeeds. The losers throw
/// <see cref="DbUpdateException"/> (whose inner exception wraps the
/// provider-specific unique-constraint error — SQLite raises
/// <c>SqliteException</c> with error code 19, PostgreSQL raises
/// <c>PostgresException</c> with SQLSTATE 23505, SQL Server raises
/// <c>SqlException</c> 2627 / 2601). We catch
/// <see cref="DbUpdateException"/> broadly here rather than
/// inspecting the inner provider exception so the same code path
/// holds across all three providers; the PK-violation re-probe
/// confirms the conflict is genuinely a duplicate before returning
/// <c>false</c>.
/// </para>
/// </remarks>
public sealed class PersistentDeduplicationService : IDeduplicationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PersistentDeduplicationService> _logger;

    public PersistentDeduplicationService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<PersistentDeduplicationService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> TryReserveAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        db.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = eventId,
            ReservedAt = now,
            ProcessedAt = null,
        });

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Re-probe: a DbUpdateException on the INSERT is overwhelmingly
            // the PK-uniqueness conflict that this method is designed to
            // gate. The re-probe confirms a row really does exist for
            // this event id before returning false — any other category
            // of DB failure (e.g. connection drop) will surface as a
            // genuine missing row and re-throw below.
            var exists = await db.ProcessedEvents
                .AsNoTracking()
                .AnyAsync(x => x.EventId == eventId, ct)
                .ConfigureAwait(false);
            if (exists)
            {
                _logger.LogDebug(
                    "Duplicate reservation suppressed for event id {EventId}; conflicting row already present.",
                    eventId);
                return false;
            }

            _logger.LogError(
                ex,
                "TryReserveAsync failed for event id {EventId} and re-probe found no row; surfacing exception to caller.",
                eventId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseReservationAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var row = await db.ProcessedEvents
            .FirstOrDefaultAsync(x => x.EventId == eventId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            // No row to release — implementations are required to be
            // idempotent and to no-op when the event id was never
            // reserved (IDeduplicationService XML doc).
            return;
        }

        if (row.ProcessedAt is not null)
        {
            // Sticky-processed guard: the row already carries a
            // MarkProcessedAsync marker, which must NOT be undone by a
            // release call. A misbehaving (or racing) release path must
            // be a no-op here — the processed marker is sticky.
            return;
        }

        db.ProcessedEvents.Remove(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker already removed (or completed) the row
            // between our read and the delete. Either outcome is
            // acceptable: if the row was deleted, the next reservation
            // will succeed normally; if it was promoted to processed,
            // the sticky-processed guard above is the desired final
            // state. No-op.
            _logger.LogDebug(
                "Concurrent change to processed_events row for event id {EventId} during release; treating as no-op.",
                eventId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsProcessedAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        return await db.ProcessedEvents
            .AsNoTracking()
            .AnyAsync(x => x.EventId == eventId && x.ProcessedAt != null, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkProcessedAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = await db.ProcessedEvents
            .FirstOrDefaultAsync(x => x.EventId == eventId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // No prior reservation — tooling-replay path or direct
            // write-through. Insert a fully-processed row in one
            // shot. The reserved/processed timestamps are deliberately
            // identical here so the cleanup sweep evicts the row at
            // exactly the same TTL boundary as a normal reservation
            // → processed lifecycle.
            var pending = new ProcessedEvent
            {
                EventId = eventId,
                ReservedAt = now,
                ProcessedAt = now,
            };
            db.ProcessedEvents.Add(pending);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // A concurrent TryReserveAsync slipped in between our
                // FirstOrDefault and our SaveChanges. The failed Added
                // entity is still tracked by `db` — if we leave it
                // attached, PromoteToProcessedAsync's FirstOrDefault
                // call against the same DbContext may resolve the
                // tracked Added instance from the identity map (EF's
                // first-level cache) instead of issuing a fresh SELECT
                // against the live DB row that the racing INSERT wrote.
                // That would leave the row unpromoted (the tracked
                // Added entity's ProcessedAt mutation would never
                // SaveChanges-replay against the existing PK row).
                // Detach the failed insert and clear all change-tracker
                // state for this DbContext so the recovery path sees
                // the live row.
                db.Entry(pending).State = EntityState.Detached;
                db.ChangeTracker.Clear();
                await PromoteToProcessedAsync(db, eventId, now, ct).ConfigureAwait(false);
            }
            return;
        }

        if (existing.ProcessedAt is not null)
        {
            // Already marked processed — sticky. Refreshing the
            // timestamp would defeat the sliding-window TTL contract
            // (a hot duplicate-storm event would never age out), so
            // leave the existing marker untouched.
            return;
        }

        existing.ProcessedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Concurrent-insert race recovery for the no-prior-reservation
    /// branch of <see cref="MarkProcessedAsync"/>. Loads the row that
    /// a racing <see cref="TryReserveAsync"/> just inserted and
    /// promotes it to the sticky-processed state via UPDATE.
    /// </summary>
    private static async Task PromoteToProcessedAsync(
        MessagingDbContext db,
        string eventId,
        DateTime now,
        CancellationToken ct)
    {
        var row = await db.ProcessedEvents
            .FirstOrDefaultAsync(x => x.EventId == eventId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            // The racing reservation must have been rolled back; the
            // row genuinely does not exist. Re-insert as a fully
            // processed marker. This branch is extremely rare in
            // practice — the original SaveChanges would have surfaced
            // any non-conflict error long before we got here.
            db.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = eventId,
                ReservedAt = now,
                ProcessedAt = now,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        if (row.ProcessedAt is null)
        {
            row.ProcessedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static void ValidateEventId(string eventId)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            throw new ArgumentException("eventId must be non-null and non-empty.", nameof(eventId));
        }
    }
}
