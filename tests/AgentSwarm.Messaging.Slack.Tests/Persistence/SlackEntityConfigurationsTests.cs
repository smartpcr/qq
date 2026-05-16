// -----------------------------------------------------------------------
// <copyright file="SlackEntityConfigurationsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using System.Linq;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

/// <summary>
/// Stage 2.2 tests for the EF Core entity configurations registered on
/// <see cref="SlackTestDbContext"/>. Implements the three brief
/// scenarios:
/// <list type="bullet">
///   <item><description>Entity configuration applies indexes (audit
///   entry has all six required indexes).</description></item>
///   <item><description>Thread mapping unique constraint
///   (duplicate <c>(TeamId, ChannelId, ThreadTs)</c> insert throws
///   <see cref="DbUpdateException"/>).</description></item>
///   <item><description>Workspace config array conversion
///   (string-array properties round-trip through SQLite).</description></item>
/// </list>
/// </summary>
public sealed class SlackEntityConfigurationsTests
{
    [Fact]
    public void SlackAuditEntry_has_all_required_indexes()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType auditType = db.Model.FindEntityType(typeof(SlackAuditEntry))
            ?? throw new InvalidOperationException("SlackAuditEntry entity not registered.");

        IIndex[] indexes = auditType.GetIndexes().ToArray();

        AssertSingleColumnIndex(indexes,
            SlackAuditEntryConfiguration.CorrelationIdIndexName,
            nameof(SlackAuditEntry.CorrelationId));

        AssertSingleColumnIndex(indexes,
            SlackAuditEntryConfiguration.TaskIdIndexName,
            nameof(SlackAuditEntry.TaskId));

        AssertSingleColumnIndex(indexes,
            SlackAuditEntryConfiguration.AgentIdIndexName,
            nameof(SlackAuditEntry.AgentId));

        AssertSingleColumnIndex(indexes,
            SlackAuditEntryConfiguration.UserIdIndexName,
            nameof(SlackAuditEntry.UserId));

        AssertSingleColumnIndex(indexes,
            SlackAuditEntryConfiguration.TimestampIndexName,
            nameof(SlackAuditEntry.Timestamp));

        IIndex composite = indexes.Should().ContainSingle(
            ix => ix.GetDatabaseName() == SlackAuditEntryConfiguration.TeamIdChannelIdIndexName,
            because: "architecture.md section 3.5 calls for a composite (team_id, channel_id) index").Subject;

        composite.Properties.Select(p => p.Name).Should().Equal(
            new[] { nameof(SlackAuditEntry.TeamId), nameof(SlackAuditEntry.ChannelId) });
    }

    [Fact]
    public void SlackAuditEntry_primary_key_is_Id()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType auditType = db.Model.FindEntityType(typeof(SlackAuditEntry))!;
        IKey pk = auditType.FindPrimaryKey()
            ?? throw new InvalidOperationException("Primary key missing.");

        pk.Properties.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(SlackAuditEntry.Id));
    }

    [Fact]
    public void SlackThreadMapping_primary_key_is_TaskId()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType threadType = db.Model.FindEntityType(typeof(SlackThreadMapping))!;
        IKey pk = threadType.FindPrimaryKey()
            ?? throw new InvalidOperationException("Primary key missing.");

        pk.Properties.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(SlackThreadMapping.TaskId));
    }

    [Fact]
    public void SlackInboundRequestRecord_primary_key_is_IdempotencyKey()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType inboundType = db.Model.FindEntityType(typeof(SlackInboundRequestRecord))!;
        IKey pk = inboundType.FindPrimaryKey()
            ?? throw new InvalidOperationException("Primary key missing.");

        pk.Properties.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(SlackInboundRequestRecord.IdempotencyKey));
    }

    [Fact]
    public void SlackInboundRequestRecord_has_first_seen_at_index()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType inboundType = db.Model.FindEntityType(typeof(SlackInboundRequestRecord))!;
        IIndex[] indexes = inboundType.GetIndexes().ToArray();

        AssertSingleColumnIndex(indexes,
            SlackInboundRequestRecordConfiguration.FirstSeenAtIndexName,
            nameof(SlackInboundRequestRecord.FirstSeenAt));
    }

    [Fact]
    public void SlackWorkspaceConfig_primary_key_is_TeamId()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType workspaceType = db.Model.FindEntityType(typeof(SlackWorkspaceConfig))!;
        IKey pk = workspaceType.FindPrimaryKey()
            ?? throw new InvalidOperationException("Primary key missing.");

        pk.Properties.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(SlackWorkspaceConfig.TeamId));
    }

    [Fact]
    public void SlackThreadMapping_has_unique_index_on_TeamId_ChannelId_ThreadTs()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType threadType = db.Model.FindEntityType(typeof(SlackThreadMapping))!;
        IIndex[] indexes = threadType.GetIndexes().Where(i => i.IsUnique).ToArray();

        indexes.Should().NotBeEmpty();
        IIndex uniqueIdx = indexes.Should().ContainSingle(ix =>
                ix.Properties.Count == 3
                && ix.Properties[0].Name == nameof(SlackThreadMapping.TeamId)
                && ix.Properties[1].Name == nameof(SlackThreadMapping.ChannelId)
                && ix.Properties[2].Name == nameof(SlackThreadMapping.ThreadTs)).Subject;

        uniqueIdx.IsUnique.Should().BeTrue();
        uniqueIdx.GetDatabaseName().Should().Be(SlackThreadMappingConfiguration.UniqueThreadIndexName);
    }

    [Fact]
    public async Task Duplicate_thread_mapping_throws_DbUpdateException()
    {
        using SlackTestDbContextFactory factory = new();

        SlackThreadMapping first = NewMapping("task-1");
        SlackThreadMapping duplicate = NewMapping("task-2");

        using (SlackTestDbContext db1 = factory.CreateContext())
        {
            db1.ThreadMappings.Add(first);
            await db1.SaveChangesAsync();
        }

        using SlackTestDbContext db2 = factory.CreateContext();
        db2.ThreadMappings.Add(duplicate);

        Func<Task> act = async () => await db2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            because: "the unique index on (TeamId, ChannelId, ThreadTs) must reject a duplicate");
    }

    [Fact]
    public async Task SlackWorkspaceConfig_string_array_round_trips_through_value_converter()
    {
        using SlackTestDbContextFactory factory = new();

        SlackWorkspaceConfig original = new()
        {
            TeamId = "T0123ABCD",
            WorkspaceName = "Test Workspace",
            BotTokenSecretRef = "keyvault://bot",
            SigningSecretRef = "keyvault://sign",
            AppLevelTokenRef = null,
            DefaultChannelId = "C-DEFAULT",
            FallbackChannelId = null,
            AllowedChannelIds = new[] { "C1", "C2" },
            AllowedUserGroupIds = new[] { "G1", "G2", "G3" },
            Enabled = true,
            CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero),
        };

        using (SlackTestDbContext writer = factory.CreateContext())
        {
            writer.Workspaces.Add(original);
            await writer.SaveChangesAsync();
        }

        using SlackTestDbContext reader = factory.CreateContext();
        SlackWorkspaceConfig? loaded = await reader.Workspaces
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.TeamId == "T0123ABCD");

        loaded.Should().NotBeNull();
        loaded!.AllowedChannelIds.Should().Equal(new[] { "C1", "C2" });
        loaded.AllowedUserGroupIds.Should().Equal(new[] { "G1", "G2", "G3" });
    }

    [Fact]
    public async Task SlackWorkspaceConfig_empty_array_round_trips_through_value_converter()
    {
        using SlackTestDbContextFactory factory = new();

        SlackWorkspaceConfig original = new()
        {
            TeamId = "T0EMPTY",
            WorkspaceName = "Empty Arrays",
            BotTokenSecretRef = "keyvault://bot",
            SigningSecretRef = "keyvault://sign",
            DefaultChannelId = "C-DEFAULT",
            AllowedChannelIds = Array.Empty<string>(),
            AllowedUserGroupIds = Array.Empty<string>(),
            Enabled = false,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        using (SlackTestDbContext writer = factory.CreateContext())
        {
            writer.Workspaces.Add(original);
            await writer.SaveChangesAsync();
        }

        using SlackTestDbContext reader = factory.CreateContext();
        SlackWorkspaceConfig? loaded = await reader.Workspaces
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.TeamId == "T0EMPTY");

        loaded.Should().NotBeNull();
        loaded!.AllowedChannelIds.Should().NotBeNull().And.BeEmpty();
        loaded.AllowedUserGroupIds.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Tables_use_canonical_snake_case_names()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        db.Model.FindEntityType(typeof(SlackWorkspaceConfig))!
            .GetTableName().Should().Be(SlackWorkspaceConfigConfiguration.TableName);
        db.Model.FindEntityType(typeof(SlackThreadMapping))!
            .GetTableName().Should().Be(SlackThreadMappingConfiguration.TableName);
        db.Model.FindEntityType(typeof(SlackInboundRequestRecord))!
            .GetTableName().Should().Be(SlackInboundRequestRecordConfiguration.TableName);
        db.Model.FindEntityType(typeof(SlackAuditEntry))!
            .GetTableName().Should().Be(SlackAuditEntryConfiguration.TableName);
    }

    [Fact]
    public void All_string_columns_have_explicit_HasColumnType()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        // Verify every string property across the four entities has an
        // explicit relational column type configured (HasColumnType).
        // The Stage 2.2 brief calls out "defining column types" as a
        // first-class deliverable; this test fails fast if a future edit
        // drops a HasColumnType and reverts to provider-default mapping.
        Type[] entityTypes = new[]
        {
            typeof(SlackWorkspaceConfig),
            typeof(SlackThreadMapping),
            typeof(SlackInboundRequestRecord),
            typeof(SlackAuditEntry),
        };

        foreach (Type clrType in entityTypes)
        {
            IEntityType entity = db.Model.FindEntityType(clrType)!;
            foreach (IProperty prop in entity.GetProperties().Where(p => p.ClrType == typeof(string)))
            {
                string? configured = prop.GetColumnType();
                configured.Should().NotBeNullOrEmpty(
                    because: $"{clrType.Name}.{prop.Name} must have an explicit HasColumnType configured");
            }
        }
    }

    [Fact]
    public void Bool_and_DateTimeOffset_columns_have_explicit_HasColumnType()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType workspace = db.Model.FindEntityType(typeof(SlackWorkspaceConfig))!;
        workspace.FindProperty(nameof(SlackWorkspaceConfig.Enabled))!
            .GetColumnType().Should().Be(SlackColumnTypes.Boolean);
        workspace.FindProperty(nameof(SlackWorkspaceConfig.CreatedAt))!
            .GetColumnType().Should().Be(SlackColumnTypes.DateTimeOffset);
        workspace.FindProperty(nameof(SlackWorkspaceConfig.UpdatedAt))!
            .GetColumnType().Should().Be(SlackColumnTypes.DateTimeOffset);

        IEntityType thread = db.Model.FindEntityType(typeof(SlackThreadMapping))!;
        thread.FindProperty(nameof(SlackThreadMapping.CreatedAt))!
            .GetColumnType().Should().Be(SlackColumnTypes.DateTimeOffset);
        thread.FindProperty(nameof(SlackThreadMapping.LastMessageAt))!
            .GetColumnType().Should().Be(SlackColumnTypes.DateTimeOffset);

        IEntityType inbound = db.Model.FindEntityType(typeof(SlackInboundRequestRecord))!;
        inbound.FindProperty(nameof(SlackInboundRequestRecord.FirstSeenAt))!
            .GetColumnType().Should().Be(SlackColumnTypes.DateTimeOffset);
        inbound.FindProperty(nameof(SlackInboundRequestRecord.CompletedAt))!
            .GetColumnType().Should().Be(SlackColumnTypes.DateTimeOffset);

        IEntityType audit = db.Model.FindEntityType(typeof(SlackAuditEntry))!;
        audit.FindProperty(nameof(SlackAuditEntry.Timestamp))!
            .GetColumnType().Should().Be(SlackColumnTypes.DateTimeOffset);
    }

    [Fact]
    public void Workspace_string_array_columns_use_unbounded_unicode_type()
    {
        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        IEntityType workspace = db.Model.FindEntityType(typeof(SlackWorkspaceConfig))!;

        workspace.FindProperty(nameof(SlackWorkspaceConfig.AllowedChannelIds))!
            .GetColumnType().Should().Be(SlackColumnTypes.UnicodeStringMax);
        workspace.FindProperty(nameof(SlackWorkspaceConfig.AllowedUserGroupIds))!
            .GetColumnType().Should().Be(SlackColumnTypes.UnicodeStringMax);
    }

    private static SlackThreadMapping NewMapping(string taskId) => new()
    {
        TaskId = taskId,
        TeamId = "T1",
        ChannelId = "C1",
        ThreadTs = "1700000000.000100",
        CorrelationId = "corr-1",
        AgentId = "agent-1",
        CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LastMessageAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static void AssertSingleColumnIndex(IIndex[] indexes, string indexName, string propertyName)
    {
        IIndex idx = indexes.Should().ContainSingle(
            ix => ix.GetDatabaseName() == indexName,
            because: $"the configuration must register an index named {indexName}").Subject;
        idx.Properties.Should().ContainSingle()
            .Which.Name.Should().Be(propertyName);
    }
}
