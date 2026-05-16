// -----------------------------------------------------------------------
// <copyright file="InboundUpdateConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// EF Core configuration for <see cref="InboundUpdate"/> — the durable inbox
/// row used to suppress duplicate Telegram webhook deliveries (FR-005, AC-003).
/// Aligned with the <c>inbound_updates</c> table shape created by the
/// <c>AddInboundUpdates</c> / <c>AddInboundProcessingStartedAt</c> migrations
/// and pinned in <c>MessagingDbContextModelSnapshot</c> so a future
/// <c>EnsureCreated</c> code path produces the same schema the migrations
/// already deploy.
/// </summary>
public sealed class InboundUpdateConfiguration : IEntityTypeConfiguration<InboundUpdate>
{
    /// <summary>
    /// Converter that maps a <see cref="DateTimeOffset"/> CLR value to a
    /// SQLite-friendly Unix-millisecond <see cref="long"/> column. The
    /// migration / snapshot pin <c>ReceivedAt</c>, <c>ProcessedAt</c>, and
    /// <c>ProcessingStartedAt</c> as <c>INTEGER</c> columns precisely so the
    /// recovery-sweep queries can ORDER BY them — SQLite cannot translate
    /// <c>DateTimeOffset</c> ORDER BY clauses on its own, but it sorts
    /// long-typed columns natively.
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableDateTimeOffsetToUnixMillis =
        new(
            v => v == null ? (long?)null : v.Value.ToUnixTimeMilliseconds(),
            v => v == null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(v.Value));

    public void Configure(EntityTypeBuilder<InboundUpdate> builder)
    {
        builder.ToTable("inbound_updates");

        // The Telegram update_id is globally unique per bot and is the canonical
        // dedup key. Using it as the primary key gives us the unique-index guard
        // we need for "exactly-once" inbox semantics — no separate index required.
        builder.HasKey(x => x.UpdateId);

        builder.Property(x => x.UpdateId)
            .ValueGeneratedNever();

        builder.Property(x => x.ReceivedAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        builder.Property(x => x.ProcessedAt)
            .HasConversion(NullableDateTimeOffsetToUnixMillis);

        builder.Property(x => x.ProcessingStartedAt)
            .HasConversion(NullableDateTimeOffsetToUnixMillis);

        builder.Property(x => x.RawPayload)
            .IsRequired();

        builder.Property(x => x.IdempotencyStatus)
            .HasConversion<string>()
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(x => x.AttemptCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128);

        // Composite index on (IdempotencyStatus, AttemptCount) accelerates the
        // recovery sweep's `GetRecoverableAsync` query, which filters on both
        // columns to find rows eligible for replay.
        builder.HasIndex(x => new { x.IdempotencyStatus, x.AttemptCount })
            .HasDatabaseName("ix_inbound_updates_status_attempt");

        // Explicit unique index on UpdateId; redundant with the PK but pinned
        // here so the constraint's name matches the migration / snapshot.
        builder.HasIndex(x => x.UpdateId)
            .IsUnique()
            .HasDatabaseName("ix_inbound_updates_update_id");
    }
}
