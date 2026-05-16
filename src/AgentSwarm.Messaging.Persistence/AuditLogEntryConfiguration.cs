// -----------------------------------------------------------------------
// <copyright file="AuditLogEntryConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// EF Core configuration for <see cref="AuditLogEntry"/>. Single table
/// <c>audit_log_entries</c> backing both the general
/// <c>AuditEntry</c> path and the human-response
/// <c>HumanResponseAuditEntry</c> path (discriminated by
/// <see cref="AuditLogEntry.EntryKind"/>).
/// </summary>
public sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log_entries");

        builder.HasKey(x => x.EntryId);

        builder.Property(x => x.EntryId)
            .ValueGeneratedNever();

        builder.Property(x => x.EntryKind)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.MessageId)
            .HasMaxLength(128);

        builder.Property(x => x.UserId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.AgentId)
            .HasMaxLength(128);

        builder.Property(x => x.Action)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.Timestamp)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Details);

        builder.Property(x => x.QuestionId)
            .HasMaxLength(128);

        builder.Property(x => x.ActionValue)
            .HasMaxLength(64);

        builder.Property(x => x.Comment);

        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_audit_log_entries_correlation_id");

        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_audit_log_entries_user_id");

        builder.HasIndex(x => x.Timestamp)
            .HasDatabaseName("ix_audit_log_entries_timestamp");
    }
}
