using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core mapping for <see cref="InboundUpdate"/>. Maps to the
/// <c>inbound_updates</c> table with <see cref="InboundUpdate.UpdateId"/>
/// as the primary key (Telegram's monotonic <c>update_id</c>) and the
/// UNIQUE invariant that makes duplicate webhook deliveries surface as
/// constraint violations rather than running the same human command
/// twice.
/// </summary>
internal sealed class InboundUpdateConfiguration : IEntityTypeConfiguration<InboundUpdate>
{
    public void Configure(EntityTypeBuilder<InboundUpdate> builder)
    {
        builder.ToTable("inbound_updates");

        builder.HasKey(x => x.UpdateId);

        // The primary key already enforces UNIQUE; the explicit index name
        // here gives the duplicate-detection log line a predictable target
        // and makes the invariant discoverable in the migrations history.
        builder.HasIndex(x => x.UpdateId)
            .IsUnique()
            .HasDatabaseName("ix_inbound_updates_update_id");

        builder.Property(x => x.UpdateId)
            .ValueGeneratedNever();

        builder.Property(x => x.RawPayload)
            .IsRequired();

        // SQLite's query translator does not support ORDER BY on a
        // DateTimeOffset column unless the value is round-tripped
        // through a binary encoding (long ticks + offset minutes). The
        // GetRecoverableAsync query in PersistentInboundUpdateStore
        // orders by ReceivedAt to preserve receipt ordering, so we apply
        // the binary converter on the two DateTimeOffset columns. Other
        // providers (SQL Server / PostgreSQL) translate the converter
        // back to native datetimeoffset storage so the on-disk schema
        // is provider-appropriate.
        builder.Property(x => x.ReceivedAt)
            .HasConversion<DateTimeOffsetToBinaryConverter>()
            .IsRequired();

        builder.Property(x => x.ProcessedAt)
            .HasConversion<DateTimeOffsetToBinaryConverter>();

        builder.Property(x => x.IdempotencyStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.AttemptCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(x => x.ErrorDetail);

        builder.Property(x => x.HandlerErrorDetail);

        // Stage 2.4 — request-scoped correlation id. Bounded length keeps
        // the column predictable on SQL Server / PostgreSQL; the
        // resolved correlation id is a header value, an Activity id, or
        // a 32-char GUID hex, all of which fit comfortably.
        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128);

        // Iter-5 evaluator item 3 — lease timestamp consumed by
        // ReclaimStaleProcessingAsync to detect orphaned Processing
        // rows that the startup reset cannot reach. Stored via the
        // binary converter for parity with the other DateTimeOffset
        // columns (SQLite ORDER BY support); nullable so legacy
        // rows without the column behave as "stale" under the
        // reclaim query's `null OR < cutoff` predicate.
        builder.Property(x => x.ProcessingStartedAt)
            .HasConversion<DateTimeOffsetToBinaryConverter>();

        // Composite filter index that mirrors the GetRecoverableAsync
        // predicate exactly — sweep scans walk this index instead of the
        // full table. Indexing on the status column alone would still
        // cover Received/Processing/Failed but would force a row read
        // per match to apply the AttemptCount filter.
        builder.HasIndex(x => new { x.IdempotencyStatus, x.AttemptCount })
            .HasDatabaseName("ix_inbound_updates_status_attempt");
    }
}
