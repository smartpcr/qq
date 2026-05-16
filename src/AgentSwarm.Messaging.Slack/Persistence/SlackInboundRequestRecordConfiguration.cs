// -----------------------------------------------------------------------
// <copyright file="SlackInboundRequestRecordConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="SlackInboundRequestRecord"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The primary key is <see cref="SlackInboundRequestRecord.IdempotencyKey"/>
/// (the dedup anchor across Slack's at-least-once redelivery semantics).
/// A non-unique index on <see cref="SlackInboundRequestRecord.FirstSeenAt"/>
/// supports the retention sweep that prunes records older than the
/// idempotency-window TTL.
/// </para>
/// </remarks>
public sealed class SlackInboundRequestRecordConfiguration
    : IEntityTypeConfiguration<SlackInboundRequestRecord>
{
    /// <summary>
    /// Canonical snake_case table name (Stage 2.3 schema test).
    /// </summary>
    public const string TableName = "slack_inbound_request_record";

    /// <summary>
    /// Name of the retention-sweep index on
    /// <see cref="SlackInboundRequestRecord.FirstSeenAt"/>.
    /// </summary>
    public const string FirstSeenAtIndexName =
        "IX_SlackInboundRequestRecord_FirstSeenAt";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SlackInboundRequestRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(TableName);

        builder.HasKey(e => e.IdempotencyKey);

        builder.Property(e => e.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasColumnType(SlackColumnTypes.UnicodeString(256))
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.SourceType)
            .HasColumnName("source_type")
            .HasColumnType(SlackColumnTypes.UnicodeString(32))
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.TeamId)
            .HasColumnName("team_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.ChannelId)
            .HasColumnName("channel_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(e => e.UserId)
            .HasColumnName("user_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.RawPayloadHash)
            .HasColumnName("raw_payload_hash")
            .HasColumnType(SlackColumnTypes.UnicodeString(128))
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.ProcessingStatus)
            .HasColumnName("processing_status")
            .HasColumnType(SlackColumnTypes.UnicodeString(32))
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.FirstSeenAt)
            .HasColumnName("first_seen_at")
            .HasColumnType(SlackColumnTypes.DateTimeOffset)
            .IsRequired();

        builder.Property(e => e.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType(SlackColumnTypes.DateTimeOffset)
            .IsRequired(false);

        builder.HasIndex(e => e.FirstSeenAt)
            .HasDatabaseName(FirstSeenAtIndexName);
    }
}
