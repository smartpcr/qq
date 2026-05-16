// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceConfigConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.Linq;
using System.Text.Json;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// EF Core configuration for <see cref="SlackWorkspaceConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Defines the column mapping, primary key on <see cref="SlackWorkspaceConfig.TeamId"/>,
/// and the value conversion that round-trips
/// <see cref="SlackWorkspaceConfig.AllowedChannelIds"/> and
/// <see cref="SlackWorkspaceConfig.AllowedUserGroupIds"/> through a JSON
/// payload column so the schema is portable across providers (SQLite is
/// used by integration tests; Postgres / SQL Server are valid production
/// targets) without requiring provider-specific array support.
/// </para>
/// <para>
/// Production registers this configuration on the upstream
/// <c>MessagingDbContext</c> in the Persistence project; tests register it
/// on <c>SlackTestDbContext</c>.
/// </para>
/// </remarks>
public sealed class SlackWorkspaceConfigConfiguration
    : IEntityTypeConfiguration<SlackWorkspaceConfig>
{
    /// <summary>
    /// Canonical snake_case table name. Stage 2.3 requires this exact
    /// table name when verifying schema creation.
    /// </summary>
    public const string TableName = "slack_workspace_config";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SlackWorkspaceConfig> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(TableName);

        builder.HasKey(e => e.TeamId);

        builder.Property(e => e.TeamId)
            .HasColumnName("team_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.WorkspaceName)
            .HasColumnName("workspace_name")
            .HasColumnType(SlackColumnTypes.UnicodeString(256))
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.BotTokenSecretRef)
            .HasColumnName("bot_token_secret_ref")
            .HasColumnType(SlackColumnTypes.UnicodeString(512))
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(e => e.SigningSecretRef)
            .HasColumnName("signing_secret_ref")
            .HasColumnType(SlackColumnTypes.UnicodeString(512))
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(e => e.AppLevelTokenRef)
            .HasColumnName("app_level_token_ref")
            .HasColumnType(SlackColumnTypes.UnicodeString(512))
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(e => e.DefaultChannelId)
            .HasColumnName("default_channel_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.FallbackChannelId)
            .HasColumnName("fallback_channel_id")
            .HasColumnType(SlackColumnTypes.UnicodeString(64))
            .HasMaxLength(64)
            .IsRequired(false);

        ValueConverter<string[], string> arrayConverter = new(
            v => JsonSerializer.Serialize(v ?? Array.Empty<string>(), JsonOpts),
            v => string.IsNullOrEmpty(v)
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(v, JsonOpts) ?? Array.Empty<string>());

        ValueComparer<string[]> arrayComparer = new(
            (a, b) => (a == null && b == null)
                || (a != null && b != null && a.SequenceEqual(b, StringComparer.Ordinal)),
            v => v == null
                ? 0
                : v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s == null ? 0 : StringComparer.Ordinal.GetHashCode(s))),
            v => v == null ? Array.Empty<string>() : v.ToArray());

        builder.Property(e => e.AllowedChannelIds)
            .HasColumnName("allowed_channel_ids")
            .HasColumnType(SlackColumnTypes.UnicodeStringMax)
            .HasConversion(arrayConverter, arrayComparer)
            .IsRequired();

        builder.Property(e => e.AllowedUserGroupIds)
            .HasColumnName("allowed_user_group_ids")
            .HasColumnType(SlackColumnTypes.UnicodeStringMax)
            .HasConversion(arrayConverter, arrayComparer)
            .IsRequired();

        builder.Property(e => e.Enabled)
            .HasColumnName("enabled")
            .HasColumnType(SlackColumnTypes.Boolean)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType(SlackColumnTypes.DateTimeOffset)
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType(SlackColumnTypes.DateTimeOffset)
            .IsRequired();
    }
}
