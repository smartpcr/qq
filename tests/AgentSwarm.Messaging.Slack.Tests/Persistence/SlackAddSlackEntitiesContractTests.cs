// -----------------------------------------------------------------------
// <copyright file="SlackAddSlackEntitiesContractTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Stage 2.3 contract tests for the
/// <see cref="SlackModelBuilderExtensions.AddSlackEntities(ModelBuilder)"/>
/// migration contribution hook -- independent of <c>SlackTestDbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// The four facts in this class exercise the hook against an arbitrary
/// upstream-style <see cref="DbContext"/> (<see cref="UpstreamStyleDbContext"/>
/// nested below) that has NO <see cref="DbSet{TEntity}"/> properties for
/// the Slack entities. This is deliberate: <see cref="DbSet{TEntity}"/>
/// properties cause EF Core to register the entity types via convention
/// even when no <c>IEntityTypeConfiguration</c> is applied. By omitting
/// the <c>DbSet</c>s, every entity type, column type, table name, and
/// index in the model must originate from
/// <see cref="SlackModelBuilderExtensions.AddSlackEntities(ModelBuilder)"/>.
/// </para>
/// <para>
/// This pins the migration-contribution contract for the future
/// upstream <c>MessagingDbContext</c> (lives in the Persistence project,
/// not yet created). Persistence cannot reference Slack directly
/// (dependency direction is Slack -> Persistence), so the upstream
/// context will receive the hook via composition-root wiring or a
/// contributor abstraction; either way it must be able to call
/// <c>builder.AddSlackEntities()</c> from its own <c>OnModelCreating</c>
/// and end up with the four canonical Slack tables.
/// </para>
/// </remarks>
public sealed class SlackAddSlackEntitiesContractTests
{
    private const string ExpectedWorkspaceTable = "slack_workspace_config";
    private const string ExpectedThreadMappingTable = "slack_thread_mapping";
    private const string ExpectedInboundRequestTable = "slack_inbound_request_record";
    private const string ExpectedAuditEntryTable = "slack_audit_entry";

    [Fact]
    public void AddSlackEntities_throws_on_null_modelBuilder()
    {
        Action act = () => SlackModelBuilderExtensions.AddSlackEntities(modelBuilder: null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("modelBuilder");
    }

    [Fact]
    public void AddSlackEntities_maps_all_four_Slack_entities_to_canonical_table_names_on_arbitrary_context()
    {
        using SqliteConnection connection = new("Filename=:memory:");
        connection.Open();
        DbContextOptions<UpstreamStyleDbContext> options =
            new DbContextOptionsBuilder<UpstreamStyleDbContext>()
                .UseSqlite(connection)
                .Options;

        using UpstreamStyleDbContext db = new(options);

        // The upstream-style context has NO DbSet<> properties, so each
        // FindEntityType result here can only succeed if
        // AddSlackEntities itself registered the type.
        db.Model.FindEntityType(typeof(SlackWorkspaceConfig))!
            .GetTableName().Should().Be(ExpectedWorkspaceTable);
        db.Model.FindEntityType(typeof(SlackThreadMapping))!
            .GetTableName().Should().Be(ExpectedThreadMappingTable);
        db.Model.FindEntityType(typeof(SlackInboundRequestRecord))!
            .GetTableName().Should().Be(ExpectedInboundRequestTable);
        db.Model.FindEntityType(typeof(SlackAuditEntry))!
            .GetTableName().Should().Be(ExpectedAuditEntryTable);
    }

    [Fact]
    public void AddSlackEntities_via_EnsureCreated_materialises_all_four_canonical_tables_on_arbitrary_context()
    {
        using SqliteConnection connection = new("Filename=:memory:");
        connection.Open();
        DbContextOptions<UpstreamStyleDbContext> options =
            new DbContextOptionsBuilder<UpstreamStyleDbContext>()
                .UseSqlite(connection)
                .Options;

        IReadOnlyList<string> before = ListSqliteUserTables(connection);
        before.Should().BeEmpty(
            because: "the in-memory database starts empty before EnsureCreated");

        using (UpstreamStyleDbContext db = new(options))
        {
            db.Database.EnsureCreated().Should().BeTrue();
        }

        IReadOnlyList<string> after = ListSqliteUserTables(connection);
        after.Should().Contain(new[]
        {
            ExpectedWorkspaceTable,
            ExpectedThreadMappingTable,
            ExpectedInboundRequestTable,
            ExpectedAuditEntryTable,
        },
        because: "the upstream-style context only learns the Slack schema through AddSlackEntities");
    }

    [Fact]
    public void AddSlackEntities_lets_arbitrary_context_round_trip_a_seeded_workspace_via_Set_T()
    {
        using SqliteConnection connection = new("Filename=:memory:");
        connection.Open();
        DbContextOptions<UpstreamStyleDbContext> options =
            new DbContextOptionsBuilder<UpstreamStyleDbContext>()
                .UseSqlite(connection)
                .Options;

        const string UpstreamTeamId = "T0UPSTRM01";

        using (UpstreamStyleDbContext writer = new(options))
        {
            writer.Database.EnsureCreated();
            SlackDbSeeder.SeedTestWorkspace(writer, UpstreamTeamId);
        }

        using UpstreamStyleDbContext reader = new(options);
        SlackWorkspaceConfig? loaded = reader.Set<SlackWorkspaceConfig>()
            .AsNoTracking()
            .SingleOrDefault(w => w.TeamId == UpstreamTeamId);

        loaded.Should().NotBeNull(
            because: "AddSlackEntities must make SlackWorkspaceConfig retrievable via Set<T>() on the upstream-style context");
        loaded!.TeamId.Should().Be(UpstreamTeamId);
        loaded.WorkspaceName.Should().Be(SlackDbSeeder.SampleWorkspaceName);
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

    /// <summary>
    /// Minimal upstream-style <see cref="DbContext"/> used to prove the
    /// <see cref="SlackModelBuilderExtensions.AddSlackEntities(ModelBuilder)"/>
    /// contract works on any context, not just <c>SlackTestDbContext</c>.
    /// </summary>
    /// <remarks>
    /// Intentionally exposes NO <see cref="DbSet{TEntity}"/> properties so
    /// that every Slack entity type, table, and index in the resulting
    /// model originates from
    /// <see cref="SlackModelBuilderExtensions.AddSlackEntities(ModelBuilder)"/>
    /// rather than from EF Core's <c>DbSet</c>-property convention.
    /// </remarks>
    private sealed class UpstreamStyleDbContext : DbContext
    {
        public UpstreamStyleDbContext(DbContextOptions<UpstreamStyleDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            modelBuilder.AddSlackEntities();
        }
    }
}
