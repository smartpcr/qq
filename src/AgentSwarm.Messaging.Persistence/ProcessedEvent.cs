// -----------------------------------------------------------------------
// <copyright file="ProcessedEvent.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;

/// <summary>
/// Stage 4.3 — one row in the durable <c>processed_events</c> sliding-
/// window table backing <see cref="PersistentDeduplicationService"/>.
/// The row's existence is what the
/// <see cref="Abstractions.IDeduplicationService.TryReserveAsync"/>
/// atomic claim turns on; the
/// <see cref="ProcessedAt"/> column transitions the row from the
/// reservation phase into the sticky "fully processed" phase set by
/// <see cref="Abstractions.IDeduplicationService.MarkProcessedAsync"/>.
/// The companion <see cref="DeduplicationCleanupService"/> evicts rows
/// older than the configured TTL so the table does not grow without
/// bound.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema (per implementation-plan.md Stage 4.3, step 3).</b> The
/// brief sketches the schema as
/// <c>processed_events(event_id TEXT PK, processed_at DATETIME)</c>.
/// This entity carries an additional <see cref="ReservedAt"/> column
/// that is REQUIRED to satisfy the
/// <see cref="Abstractions.IDeduplicationService"/> lifecycle contract
/// — specifically the distinction between the reservation phase
/// (<see cref="ProcessedAt"/> is <c>null</c>) and the sticky-processed
/// phase (<see cref="ProcessedAt"/> is non-null). Without that
/// distinction, <see cref="Abstractions.IDeduplicationService.ReleaseReservationAsync"/>
/// could not honour its "no-op if already processed" guarantee, and
/// the cleanup sweep could not safely distinguish abandoned
/// reservations (eligible for eviction by <see cref="ReservedAt"/>)
/// from successful completions (eligible for eviction by
/// <see cref="ProcessedAt"/>).
/// </para>
/// <para>
/// <b>Atomic concurrency.</b> <see cref="EventId"/> is the primary key
/// and is the sole concurrency gate against duplicate
/// <see cref="Abstractions.IDeduplicationService.TryReserveAsync"/>
/// callers — the database's UNIQUE-on-PK constraint guarantees only
/// one INSERT wins. The implementation catches
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> and
/// returns <c>false</c> for the losers; see
/// <see cref="PersistentDeduplicationService.TryReserveAsync"/>.
/// </para>
/// </remarks>
public sealed class ProcessedEvent
{
    /// <summary>
    /// The opaque event identifier from the inbound
    /// <see cref="Abstractions.MessengerEvent"/> (typically the Telegram
    /// <c>update_id</c> serialised as a string). Primary key and the
    /// single concurrency gate against duplicate <see cref="TryReserveAsync"/>
    /// races — collisions resolve via the UNIQUE-on-PK constraint at
    /// the database layer.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// UTC wall-clock instant the row was inserted (i.e. the moment the
    /// reservation was claimed by <see cref="Abstractions.IDeduplicationService.TryReserveAsync"/>
    /// or by <see cref="Abstractions.IDeduplicationService.MarkProcessedAsync"/>
    /// in the tooling-replay path where no prior reservation existed).
    /// Used by <see cref="DeduplicationCleanupService"/> as the
    /// eviction handle for abandoned reservations whose
    /// <see cref="ProcessedAt"/> never transitioned out of <c>null</c>.
    /// Stored as the snake_case <c>reserved_at DATETIME</c> column
    /// (alongside the brief-mandated <c>processed_at DATETIME</c>) to
    /// satisfy the
    /// <see cref="Abstractions.IDeduplicationService"/> two-phase
    /// reservation/sticky-processed lifecycle contract.
    /// </summary>
    public required DateTime ReservedAt { get; set; }

    /// <summary>
    /// UTC wall-clock instant
    /// <see cref="Abstractions.IDeduplicationService.MarkProcessedAsync"/>
    /// promoted the row from the reservation phase into the sticky
    /// "fully processed" phase. <c>null</c> while the routed handler
    /// is in flight; non-null once the handler completed (or the
    /// tooling-replay path wrote the marker directly). Used by
    /// <see cref="Abstractions.IDeduplicationService.IsProcessedAsync"/>
    /// as the gate (<c>EXISTS WHERE event_id = @id AND processed_at IS NOT NULL</c>)
    /// and by <see cref="DeduplicationCleanupService"/> as the
    /// preferred eviction handle for completed events. Stored as the
    /// brief-mandated <c>processed_at DATETIME</c> column.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }
}
