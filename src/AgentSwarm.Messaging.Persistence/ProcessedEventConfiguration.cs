// -----------------------------------------------------------------------
// <copyright file="ProcessedEventConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Stage 4.3 — EF Core configuration for <see cref="ProcessedEvent"/>.
/// Persists the rows backing
/// <see cref="Abstractions.IDeduplicationService"/> via
/// <see cref="PersistentDeduplicationService"/> in the
/// <c>processed_events</c> SQLite / PostgreSQL / SQL Server table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema shape (per implementation-plan.md Stage 4.3, step 3).</b>
/// The brief specifies
/// <c>processed_events(event_id TEXT PK, processed_at DATETIME)</c>.
/// This configuration matches that shape verbatim: snake_case column
/// names, <c>event_id TEXT</c> as the primary key, and
/// <c>processed_at DATETIME</c> for the sticky-processed timestamp.
/// A companion <c>reserved_at DATETIME</c> column carries the
/// reservation timestamp (the brief sketches the keys-and-values,
/// not the two-phase reservation lifecycle that
/// <see cref="Abstractions.IDeduplicationService"/> mandates).
/// </para>
/// <para>
/// <b>Time encoding.</b> Both timestamps are stored as
/// <see cref="System.DateTime"/> (UTC) with the explicit column
/// type <c>DATETIME</c>. On SQLite, EF stores
/// <see cref="System.DateTime"/> as ISO 8601 TEXT (e.g.
/// <c>"2026-05-16 12:00:00"</c>), which sorts lexicographically and
/// therefore supports the cleanup-sweep ordered comparison
/// <c>(ProcessedAt ?? ReservedAt) &lt; cutoff</c> without a value
/// converter. On SQL Server the column maps to the legacy
/// <c>DATETIME</c> type (validator accepts the
/// <see cref="System.DateTime"/> → <c>DATETIME</c> mapping).
/// PostgreSQL deployments must generate provider-specific
/// migrations (see
/// <see cref="DesignTimeMessagingDbContextFactory"/>) that emit
/// <c>timestamp without time zone</c> in place of <c>DATETIME</c>.
/// </para>
/// <para>
/// <b>Indexes.</b> <c>event_id</c> is the primary key — the implicit
/// PK index is also the atomic-claim gate for
/// <see cref="Abstractions.IDeduplicationService.TryReserveAsync"/>
/// (UNIQUE constraint on the PK rejects duplicate inserts at the
/// database boundary). The composite
/// <c>ix_processed_events_processed_reserved</c> index on
/// <c>(processed_at, reserved_at)</c> backs the periodic purge
/// query in <see cref="DeduplicationCleanupService"/>: the sweep
/// computes <c>COALESCE(processed_at, reserved_at) &lt; cutoff</c>
/// against a configurable TTL, and indexing both columns keeps the
/// scan narrow even as the table swells under burst load.
/// </para>
/// </remarks>
public sealed class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
    {
        builder.ToTable("processed_events");

        builder.HasKey(x => x.EventId);

        builder.Property(x => x.EventId)
            .HasColumnName("event_id")
            .HasColumnType("TEXT")
            .HasMaxLength(128)
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(x => x.ReservedAt)
            .HasColumnName("reserved_at")
            .HasColumnType("DATETIME")
            .IsRequired();

        builder.Property(x => x.ProcessedAt)
            .HasColumnName("processed_at")
            .HasColumnType("DATETIME");

        // Cleanup sweep predicate: COALESCE(processed_at, reserved_at) < cutoff.
        // Both columns are stored as DATETIME (ISO 8601 TEXT on
        // SQLite), and SQLite + SQL Server EF providers translate
        // the ?? operator to CASE/COALESCE with lexicographic
        // comparison. The composite index keeps the scan narrow
        // when the table holds thousands of rows during a burst.
        builder.HasIndex(x => new { x.ProcessedAt, x.ReservedAt })
            .HasDatabaseName("ix_processed_events_processed_reserved");
    }
}
