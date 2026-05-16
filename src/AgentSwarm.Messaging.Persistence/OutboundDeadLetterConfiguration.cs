// -----------------------------------------------------------------------
// <copyright file="OutboundDeadLetterConfiguration.cs" company="Microsoft Corp.">
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
/// EF Core configuration for <see cref="OutboundDeadLetterRecord"/> —
/// the durable dead-letter ledger written by
/// <c>TelegramMessageSender</c> when a Telegram send exhausts its
/// in-sender retry budget (iter-4 evaluator item 4). The ledger is
/// the answer to the "If Telegram send fails, message is retried and
/// eventually dead-lettered with alert" acceptance criterion BEFORE
/// Stage 4.1's outbox-row DLQ path lands.
/// </summary>
public sealed class OutboundDeadLetterConfiguration
    : IEntityTypeConfiguration<OutboundDeadLetterRecord>
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    private static readonly ValueConverter<OutboundFailureCategory, string> FailureCategoryToString =
        new(
            v => v.ToString(),
            v => (OutboundFailureCategory)Enum.Parse(typeof(OutboundFailureCategory), v));

    public void Configure(EntityTypeBuilder<OutboundDeadLetterRecord> builder)
    {
        builder.ToTable("outbound_dead_letters");

        builder.HasKey(x => x.DeadLetterId);

        builder.Property(x => x.DeadLetterId)
            .ValueGeneratedNever();

        builder.Property(x => x.ChatId)
            .IsRequired();

        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.AttemptCount)
            .IsRequired();

        builder.Property(x => x.FailureCategory)
            .HasConversion(FailureCategoryToString)
            .HasMaxLength(48)
            .IsRequired();

        builder.Property(x => x.LastErrorType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.LastErrorMessage)
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(x => x.FailedAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        // Operator-facing audit queries pivot on CorrelationId (trace
        // pivot from logs) and on ChatId (per-chat dead-letter
        // history). Two non-unique secondary indexes keep both
        // queries O(log n).
        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_outbound_dlq_correlation_id");

        builder.HasIndex(x => x.ChatId)
            .HasDatabaseName("ix_outbound_dlq_chat_id");
    }
}
