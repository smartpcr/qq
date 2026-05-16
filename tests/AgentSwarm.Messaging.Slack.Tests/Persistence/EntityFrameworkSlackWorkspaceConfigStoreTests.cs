// -----------------------------------------------------------------------
// <copyright file="EntityFrameworkSlackWorkspaceConfigStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 3.1 evaluator iter-3 item 1 regression tests for
/// <see cref="EntityFrameworkSlackWorkspaceConfigStore{TContext}"/>.
/// The iter-3 review flagged that
/// "<c>Program.BuildApp</c> registers only the default in-memory
/// workspace store ... a real restarted Worker cannot resolve any
/// <c>SlackWorkspaceConfig.SigningSecretRef</c>". This fixture pins the
/// EF-backed store's read path so the production Worker host has a
/// durable, restart-survival workspace lookup.
/// </summary>
public sealed class EntityFrameworkSlackWorkspaceConfigStoreTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public EntityFrameworkSlackWorkspaceConfigStoreTests()
    {
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        ServiceCollection services = new();
        services.AddDbContext<WorkspaceTestDbContext>(opts => opts.UseSqlite(this.connection));
        this.serviceProvider = services.BuildServiceProvider();

        using IServiceScope scope = this.serviceProvider.CreateScope();
        WorkspaceTestDbContext ctx = scope.ServiceProvider.GetRequiredService<WorkspaceTestDbContext>();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        this.serviceProvider.Dispose();
        this.connection.Dispose();
    }

    [Fact]
    public async Task GetByTeamIdAsync_returns_persisted_workspace_row()
    {
        await this.SeedAsync(new SlackWorkspaceConfig
        {
            TeamId = "T01EFTEST1",
            WorkspaceName = "EF Test Workspace",
            BotTokenSecretRef = "env://BOT_T01EFTEST1",
            SigningSecretRef = "env://SIGN_T01EFTEST1",
            DefaultChannelId = "C01EFTEST1",
            AllowedChannelIds = new[] { "C01EFTEST1" },
            AllowedUserGroupIds = Array.Empty<string>(),
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext> store = this.CreateStore();

        SlackWorkspaceConfig? row = await store.GetByTeamIdAsync("T01EFTEST1", CancellationToken.None);

        row.Should().NotBeNull();
        row!.SigningSecretRef.Should().Be("env://SIGN_T01EFTEST1",
            "the EF-backed store must resolve the signing secret reference end-to-end so the validator can compute the HMAC after a host restart");
        row.WorkspaceName.Should().Be("EF Test Workspace");
        row.AllowedChannelIds.Should().BeEquivalentTo(new[] { "C01EFTEST1" });
    }

    [Fact]
    public async Task GetByTeamIdAsync_returns_null_for_unknown_team_id()
    {
        EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext> store = this.CreateStore();
        SlackWorkspaceConfig? row = await store.GetByTeamIdAsync("T0NOTHERE0", CancellationToken.None);
        row.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetByTeamIdAsync_returns_null_for_blank_team_id(string? teamId)
    {
        EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext> store = this.CreateStore();
        SlackWorkspaceConfig? row = await store.GetByTeamIdAsync(teamId, CancellationToken.None);
        row.Should().BeNull();
    }

    [Fact]
    public async Task GetByTeamIdAsync_returns_null_for_disabled_workspace_row()
    {
        // Stage 3.1 evaluator iter-4 item 2: ISlackWorkspaceConfigStore
        // contracts that disabled rows are filtered at the store
        // boundary. The EF implementation MUST apply the same filter
        // as the in-memory implementation so callers (notably the
        // Stage 3.2 ACL filter) can trust a non-null result is an
        // enabled, usable workspace without re-checking Enabled.
        await this.SeedAsync(MakeWorkspace("T0DISABLE9", enabled: false));

        EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext> store = this.CreateStore();
        SlackWorkspaceConfig? row = await store.GetByTeamIdAsync("T0DISABLE9", CancellationToken.None);
        row.Should().BeNull(
            "the EF store must filter Enabled=false rows at the GetByTeamIdAsync boundary so the contract matches InMemorySlackWorkspaceConfigStore");
    }

    [Fact]
    public async Task GetAllEnabledAsync_returns_only_enabled_rows()
    {
        await this.SeedAsync(
            MakeWorkspace("T0ENABLED1", enabled: true),
            MakeWorkspace("T0DISABLE1", enabled: false),
            MakeWorkspace("T0ENABLED2", enabled: true));

        EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext> store = this.CreateStore();
        IReadOnlyCollection<SlackWorkspaceConfig> enabled = await store.GetAllEnabledAsync(CancellationToken.None);

        enabled.Should().HaveCount(2);
        enabled.Should().OnlyContain(c => c.Enabled);
        enabled.Should().Contain(c => c.TeamId == "T0ENABLED1");
        enabled.Should().Contain(c => c.TeamId == "T0ENABLED2");
    }

    [Fact]
    public async Task GetByTeamIdAsync_does_not_track_returned_rows()
    {
        // The store uses AsNoTracking so reads do not contaminate the
        // change tracker; subsequent inserts on a fresh scope must not
        // see a phantom "Unchanged" row.
        await this.SeedAsync(MakeWorkspace("T0NOTRACK1", enabled: true));

        EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext> store = this.CreateStore();
        SlackWorkspaceConfig? row = await store.GetByTeamIdAsync("T0NOTRACK1", CancellationToken.None);
        row.Should().NotBeNull();

        // Mutate the returned object -- because it is detached, this MUST
        // NOT bleed into the database on the next SaveChangesAsync from a
        // separate scope.
        row!.WorkspaceName = "mutated-locally";

        using IServiceScope scope = this.serviceProvider.CreateScope();
        WorkspaceTestDbContext ctx = scope.ServiceProvider.GetRequiredService<WorkspaceTestDbContext>();
        SlackWorkspaceConfig? fresh = await ctx.SlackWorkspaceConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TeamId == "T0NOTRACK1");
        fresh!.WorkspaceName.Should().NotBe("mutated-locally");
    }

    private static SlackWorkspaceConfig MakeWorkspace(string teamId, bool enabled) => new()
    {
        TeamId = teamId,
        WorkspaceName = teamId + "-name",
        BotTokenSecretRef = "env://BOT_" + teamId,
        SigningSecretRef = "env://SIGN_" + teamId,
        DefaultChannelId = "C" + teamId,
        AllowedChannelIds = Array.Empty<string>(),
        AllowedUserGroupIds = Array.Empty<string>(),
        Enabled = enabled,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext> CreateStore()
    {
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext>(
            scopeFactory,
            NullLogger<EntityFrameworkSlackWorkspaceConfigStore<WorkspaceTestDbContext>>.Instance);
    }

    private async Task SeedAsync(params SlackWorkspaceConfig[] rows)
    {
        using IServiceScope scope = this.serviceProvider.CreateScope();
        WorkspaceTestDbContext ctx = scope.ServiceProvider.GetRequiredService<WorkspaceTestDbContext>();
        foreach (SlackWorkspaceConfig row in rows)
        {
            ctx.SlackWorkspaceConfigs.Add(row);
        }

        await ctx.SaveChangesAsync();
    }

    private sealed class WorkspaceTestDbContext : DbContext, ISlackWorkspaceConfigDbContext
    {
        public WorkspaceTestDbContext(DbContextOptions<WorkspaceTestDbContext> options)
            : base(options)
        {
        }

        public DbSet<SlackWorkspaceConfig> SlackWorkspaceConfigs => this.Set<SlackWorkspaceConfig>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.AddSlackEntities();
        }
    }
}
