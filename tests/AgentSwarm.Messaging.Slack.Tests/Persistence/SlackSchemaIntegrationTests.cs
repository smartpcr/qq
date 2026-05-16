// -----------------------------------------------------------------------
// <copyright file="SlackSchemaIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

/// <summary>
/// Stage 2.3 integration tests for the Slack persistence schema. Covers
/// the two scenarios spelled out in the brief:
/// <list type="bullet">
///   <item><description>Schema creates all four tables when
///   <see cref="DatabaseFacade.EnsureCreated"/> runs against a clean
///   SQLite in-memory database.</description></item>
///   <item><description>
///   <see cref="SlackDbSeeder.SeedTestWorkspace(DbContext, string?)"/>
///   inserts a row retrievable by <see cref="SlackWorkspaceConfig.TeamId"/>.
///   </description></item>
/// </list>
/// Plus a guard that proves the four Slack entity configurations are
/// auto-discovered by <see cref="SlackTestDbContext"/> via
/// <see cref="ModelBuilderExtensions.ApplyConfigurationsFromAssembly(ModelBuilder, Assembly, Func{Type, bool}?)"/>.
/// </summary>
public sealed class SlackSchemaIntegrationTests
{
    // Literal canonical table names as spelled out in the Stage 2.3
    // brief and the implementation-plan acceptance scenarios. Held as
    // string literals (NOT derived from
    // SlackXxxConfiguration.TableName constants) so an accidental rename
    // of a production constant does NOT silently update the test
    // expectation -- a divergence between the brief's required names and
    // the configurations' constants will fail this test instead.
    private const string ExpectedWorkspaceTable = "slack_workspace_config";
    private const string ExpectedThreadMappingTable = "slack_thread_mapping";
    private const string ExpectedInboundRequestTable = "slack_inbound_request_record";
    private const string ExpectedAuditEntryTable = "slack_audit_entry";

    private static readonly IReadOnlyList<string> ExpectedTableNames = new[]
    {
        ExpectedWorkspaceTable,
        ExpectedThreadMappingTable,
        ExpectedInboundRequestTable,
        ExpectedAuditEntryTable,
    };

    [Fact]
    public void Canonical_table_name_constants_match_brief_required_names()
    {
        // Pins the Stage 2.2 configuration constants to the exact
        // canonical strings spelled in the Stage 2.3 brief and the
        // implementation-plan scenario. If a future edit renames any of
        // SlackXxxConfiguration.TableName, this test fails (and the
        // schema test still independently asserts the literals, so the
        // two layers cannot drift together).
        SlackWorkspaceConfigConfiguration.TableName
            .Should().Be(ExpectedWorkspaceTable,
                because: "the brief requires the canonical name 'slack_workspace_config' verbatim");
        SlackThreadMappingConfiguration.TableName
            .Should().Be(ExpectedThreadMappingTable,
                because: "the brief requires the canonical name 'slack_thread_mapping' verbatim");
        SlackInboundRequestRecordConfiguration.TableName
            .Should().Be(ExpectedInboundRequestTable,
                because: "the brief requires the canonical name 'slack_inbound_request_record' verbatim");
        SlackAuditEntryConfiguration.TableName
            .Should().Be(ExpectedAuditEntryTable,
                because: "the brief requires the canonical name 'slack_audit_entry' verbatim");
    }

    [Fact]
    public void EnsureCreated_creates_all_four_canonical_snake_case_tables()
    {
        // Honest "Given a clean SQLite in-memory database, When EnsureCreated is
        // called, Then tables exist" -- the factory pre-bootstraps in its ctor
        // and so cannot be used here.
        using SqliteConnection connection = new("Filename=:memory:");
        connection.Open();

        DbContextOptions<SlackTestDbContext> options =
            new DbContextOptionsBuilder<SlackTestDbContext>()
                .UseSqlite(connection)
                .Options;

        IReadOnlyList<string> tablesBefore = ListSqliteUserTables(connection);
        tablesBefore.Should().BeEmpty(
            because: "the in-memory SQLite database starts empty before EnsureCreated runs");

        using (SlackTestDbContext db = new(options))
        {
            bool created = db.Database.EnsureCreated();
            created.Should().BeTrue(
                because: "the schema has not yet been materialised on this connection");
        }

        IReadOnlyList<string> tablesAfter = ListSqliteUserTables(connection);
        tablesAfter.Should().Contain(ExpectedTableNames,
            because: "every Slack entity configuration must materialise its canonical snake_case table");
    }

    [Fact]
    public void SeedTestWorkspace_inserts_sample_workspace_retrievable_by_TeamId()
    {
        using SlackTestDbContextFactory factory = new();

        SlackWorkspaceConfig seeded;
        using (SlackTestDbContext writer = factory.CreateContext())
        {
            seeded = SlackDbSeeder.SeedTestWorkspace(writer);
        }

        seeded.TeamId.Should().Be(SlackDbSeeder.SampleTeamId);

        // Read back through a FRESH context so we exercise the storage layer,
        // not the change tracker of the writer.
        using SlackTestDbContext reader = factory.CreateContext();
        SlackWorkspaceConfig? loaded = reader.Workspaces
            .AsNoTracking()
            .SingleOrDefault(w => w.TeamId == SlackDbSeeder.SampleTeamId);

        loaded.Should().NotBeNull(
            because: "SeedTestWorkspace must persist a row that is retrievable by TeamId");
        loaded!.WorkspaceName.Should().Be(SlackDbSeeder.SampleWorkspaceName);
        loaded.BotTokenSecretRef.Should().Be(SlackDbSeeder.SampleBotTokenSecretRef);
        loaded.SigningSecretRef.Should().Be(SlackDbSeeder.SampleSigningSecretRef);
        loaded.DefaultChannelId.Should().Be(SlackDbSeeder.SampleDefaultChannelId);
        loaded.Enabled.Should().BeTrue();
        loaded.CreatedAt.Should().Be(SlackDbSeeder.SampleCreatedAt);
        loaded.UpdatedAt.Should().Be(SlackDbSeeder.SampleUpdatedAt);
        loaded.AllowedChannelIds.Should().NotBeNullOrEmpty()
            .And.Contain(SlackDbSeeder.SampleDefaultChannelId);
        loaded.AllowedUserGroupIds.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SeedTestWorkspace_respects_explicit_team_id_override()
    {
        using SlackTestDbContextFactory factory = new();

        const string CustomTeamId = "T0CUSTOM99";

        using (SlackTestDbContext writer = factory.CreateContext())
        {
            SlackDbSeeder.SeedTestWorkspace(writer, CustomTeamId);
        }

        using SlackTestDbContext reader = factory.CreateContext();
        SlackWorkspaceConfig? loaded = reader.Workspaces
            .AsNoTracking()
            .SingleOrDefault(w => w.TeamId == CustomTeamId);

        loaded.Should().NotBeNull(
            because: "the override TeamId must reach the database verbatim");
        loaded!.TeamId.Should().Be(CustomTeamId);
    }

    [Fact]
    public void SeedTestWorkspace_throws_on_null_context()
    {
        Action act = () => SlackDbSeeder.SeedTestWorkspace(db: null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("db");
    }

    [Fact]
    public void ApplyConfigurationsFromAssembly_auto_discovers_every_slack_entity_configuration()
    {
        // Enumerate every IEntityTypeConfiguration<T> implementation in the
        // Slack assembly -- this is the contract the test context relies on
        // when it calls AddSlackEntities() (which itself calls
        // ApplyConfigurationsFromAssembly). For each discovered T, assert
        // that the test context's model reflects the configuration EFFECTS
        // (canonical snake_case table name set by HasColumnName /
        // ToTable), not just that the CLR type is registered -- the latter
        // would already be true from the DbSet<> properties on
        // SlackTestDbContext regardless of whether assembly scanning ran.
        Assembly slackAssembly = typeof(SlackModelBuilderExtensions).Assembly;

        (Type EntityType, string ExpectedTableName)[] discovered = slackAssembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
                .Select(i => (ConfigType: t, EntityType: i.GetGenericArguments()[0])))
            .Select(pair => (pair.EntityType, ExpectedTableName: ResolveExpectedTableName(pair.ConfigType)))
            .ToArray();

        discovered.Should().NotBeEmpty(
            because: "the Slack assembly must publish at least one IEntityTypeConfiguration");

        // Stage 2.3 ships exactly the four canonical Slack tables; future
        // stages that add more configurations will increase this count.
        discovered.Select(d => d.EntityType).Should().BeEquivalentTo(new[]
        {
            typeof(SlackWorkspaceConfig),
            typeof(SlackThreadMapping),
            typeof(SlackInboundRequestRecord),
            typeof(SlackAuditEntry),
        });

        using SlackTestDbContextFactory factory = new();
        using SlackTestDbContext db = factory.CreateContext();

        foreach ((Type entityType, string expectedTable) in discovered)
        {
            IEntityType? mapped = db.Model.FindEntityType(entityType);
            mapped.Should().NotBeNull(
                because: $"{entityType.Name} should be registered through ApplyConfigurationsFromAssembly");

            mapped!.GetTableName().Should().Be(expectedTable,
                because: $"{entityType.Name}'s table name proves its EF configuration actually ran (not just that the DbSet<> registered the CLR type)");
        }
    }

    private static IReadOnlyList<string> ListSqliteUserTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        List<string> names = new();
        using DbDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string ResolveExpectedTableName(Type configurationType)
    {
        // The Stage 2.2 configurations expose their canonical snake_case
        // table name as a public const string TableName. Pull the value via
        // reflection so a future configuration that ships without that
        // const will surface as a clear test failure rather than a silent
        // skip.
        FieldInfo? field = configurationType.GetField(
            "TableName",
            BindingFlags.Public | BindingFlags.Static);

        if (field is null || field.FieldType != typeof(string))
        {
            throw new InvalidOperationException(
                $"{configurationType.FullName} must expose 'public const string TableName' so the schema test can validate auto-discovery.");
        }

        return (string)field.GetValue(null)!;
    }
}
