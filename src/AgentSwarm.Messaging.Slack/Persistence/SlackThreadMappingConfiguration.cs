// -----------------------------------------------------------------------
// <copyright file="SlackThreadMappingConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="SlackThreadMapping"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Sets <see cref="SlackThreadMapping.TaskId"/> as the primary key and
/// declares the unique constraint over
/// <c>(TeamId, ChannelId, ThreadTs)</c> required by architecture.md
/// section 3.2 so the same Slack thread cannot be claimed by two
/// different agent tasks. The constraint is owned by this configuration
/// (not the entity class) so the schema-mapping surface lives in one
/// place.
/// </para>
/// </remarks>
public sealed class SlackThreadMappingConfiguration
    : IEntityTypeConfiguration<SlackThreadMapping>
{
    /// <summary>
    /// Canonical snake_case table name (Stage 2.3 schema test).
    /// </summary>
    public const string TableName = "slack_thread_mapping";

    /// <summary>
    /// Name of the unique index over <c>(TeamId, ChannelId, ThreadTs)</c>.
    /// </summary>
    public const string UniqueThreadIndexName =
        "IX_SlackThreadMapping_TeamId_ChannelId_ThreadTs";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SlackThreadMapping> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(TableName);

        builder.HasKey(e => e.TaskId);

        builder.Property(e => e.TaskId)
            .HasColumnName("task_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(128))
            .HasMaxLength(128)
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
            .IsRequired();

        builder.Property(e => e.ThreadTs)
            .HasColumnName("thread_ts")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.CorrelationId)
            .HasColumnName("correlation_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(128))
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.AgentId)
            .HasColumnName("agent_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(128))
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType(SlackColumnTypes.DateTimeOffset)
            .IsRequired();

        builder.Property(e => e.LastMessageAt)
            .HasColumnName("last_message_at")
            .HasColumnType(SlackColumnTypes.DateTimeOffset)
            .IsRequired();

        builder.HasIndex(e => new { e.TeamId, e.ChannelId, e.ThreadTs })
            .IsUnique()
            .HasDatabaseName(UniqueThreadIndexName);
    }
}
