// -----------------------------------------------------------------------
// <copyright file="SlackAuditEntryConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="SlackAuditEntry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Defines the primary key on <see cref="SlackAuditEntry.Id"/> and the
/// secondary indexes called out in architecture.md section 3.5
/// (<c>correlation_id</c>, <c>task_id</c>, <c>agent_id</c>,
/// <c>(team_id, channel_id)</c>, <c>user_id</c>, <c>timestamp</c>).
/// These indexes back the audit query API used by
/// <c>SlackAuditLogger.QueryAsync</c> in Stage 5.
/// </para>
/// </remarks>
public sealed class SlackAuditEntryConfiguration
    : IEntityTypeConfiguration<SlackAuditEntry>
{
    /// <summary>
    /// Canonical snake_case table name (Stage 2.3 schema test).
    /// </summary>
    public const string TableName = "slack_audit_entry";

    /// <summary>Index name for <c>correlation_id</c> lookups.</summary>
    public const string CorrelationIdIndexName = "IX_SlackAuditEntry_CorrelationId";

    /// <summary>Index name for <c>task_id</c> lookups.</summary>
    public const string TaskIdIndexName = "IX_SlackAuditEntry_TaskId";

    /// <summary>Index name for <c>agent_id</c> lookups.</summary>
    public const string AgentIdIndexName = "IX_SlackAuditEntry_AgentId";

    /// <summary>Index name for the composite <c>(team_id, channel_id)</c>.</summary>
    public const string TeamIdChannelIdIndexName = "IX_SlackAuditEntry_TeamId_ChannelId";

    /// <summary>Index name for <c>user_id</c> lookups.</summary>
    public const string UserIdIndexName = "IX_SlackAuditEntry_UserId";

    /// <summary>Index name for <c>timestamp</c> range scans.</summary>
    public const string TimestampIndexName = "IX_SlackAuditEntry_Timestamp";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SlackAuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(TableName);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType(SlackColumnTypes.UnicodeString(32))
            .HasMaxLength(32)
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
            .IsRequired(false);

        builder.Property(e => e.TaskId)
            .HasColumnName("task_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(128))
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(e => e.ConversationId)
            .HasColumnName("conversation_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(128))
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(e => e.Direction)
            .HasColumnName("direction")
            .HasColumnType(SlackColumnTypes.UnicodeString(16))
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(e => e.RequestType)
            .HasColumnName("request_type")
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

        builder.Property(e => e.ThreadTs)
            .HasColumnName("thread_ts")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(e => e.MessageTs)
            .HasColumnName("message_ts")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(e => e.UserId)
            .HasColumnName("user_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(e => e.CommandText)
            .HasColumnName("command_text")
            .HasColumnType(SlackColumnTypes.UnicodeStringMax)
            .IsRequired(false);

        builder.Property(e => e.ResponsePayload)
            .HasColumnName("response_payload")
            .HasColumnType(SlackColumnTypes.UnicodeStringMax)
            .IsRequired(false);

        builder.Property(e => e.Outcome)
            .HasColumnName("outcome")
            .HasColumnType(SlackColumnTypes.UnicodeString(32))
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.ErrorDetail)
            .HasColumnName("error_detail")
            .HasColumnType(SlackColumnTypes.UnicodeStringMax)
            .IsRequired(false);

        builder.Property(e => e.Timestamp)
            .HasColumnName("timestamp")
            .HasColumnType(SlackColumnTypes.DateTimeOffset)
            .IsRequired();

        builder.HasIndex(e => e.CorrelationId).HasDatabaseName(CorrelationIdIndexName);
        builder.HasIndex(e => e.TaskId).HasDatabaseName(TaskIdIndexName);
        builder.HasIndex(e => e.AgentId).HasDatabaseName(AgentIdIndexName);
        builder.HasIndex(e => new { e.TeamId, e.ChannelId }).HasDatabaseName(TeamIdChannelIdIndexName);
        builder.HasIndex(e => e.UserId).HasDatabaseName(UserIdIndexName);
        builder.HasIndex(e => e.Timestamp).HasDatabaseName(TimestampIndexName);
    }
}
