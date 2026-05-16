// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceConfigSeedHostedServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 3.1 evaluator iter-3 item 1 regression tests for
/// <see cref="SlackWorkspaceConfigSeedHostedService{TContext}"/>. The
/// seeder converts <c>Slack:Workspaces</c> configuration entries into
/// idempotent upserts against the EF-backed
/// <c>slack_workspace_config</c> table so a freshly-deployed host has
/// the configured workspaces available without out-of-band DB seeding.
/// </summary>
public sealed class SlackWorkspaceConfigSeedHostedServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public SlackWorkspaceConfigSeedHostedServiceTests()
    {
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        ServiceCollection services = new();
        services.AddDbContext<SeederTestDbContext>(opts => opts.UseSqlite(this.connection));
        this.serviceProvider = services.BuildServiceProvider();

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SeederTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SeederTestDbContext>();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        this.serviceProvider.Dispose();
        this.connection.Dispose();
    }

    [Fact]
    public async Task StartAsync_inserts_new_workspaces_from_config()
    {
        SlackWorkspaceConfigSeedHostedService<SeederTestDbContext> seeder = this.CreateSeeder(
            new SlackWorkspaceSeedEntry
            {
                TeamId = "T0SEED0001",
                WorkspaceName = "Seed Workspace",
                BotTokenSecretRef = "env://BOT",
                SigningSecretRef = "env://SIGN",
                Enabled = true,
            });

        await seeder.StartAsync(CancellationToken.None);

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SeederTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SeederTestDbContext>();
        SlackWorkspaceConfig? row = await ctx.SlackWorkspaceConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TeamId == "T0SEED0001");

        row.Should().NotBeNull();
        row!.WorkspaceName.Should().Be("Seed Workspace");
        row.SigningSecretRef.Should().Be("env://SIGN");
        row.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_is_idempotent_when_run_twice()
    {
        SlackWorkspaceSeedEntry entry = new()
        {
            TeamId = "T0IDEM0001",
            WorkspaceName = "Idempotent Workspace",
            SigningSecretRef = "env://IDEM",
            Enabled = true,
        };

        await this.CreateSeeder(entry).StartAsync(CancellationToken.None);
        await this.CreateSeeder(entry).StartAsync(CancellationToken.None);

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SeederTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SeederTestDbContext>();
        int count = await ctx.SlackWorkspaceConfigs.CountAsync(c => c.TeamId == "T0IDEM0001");
        count.Should().Be(1, "the seeder must upsert by team_id rather than inserting duplicate rows");
    }

    [Fact]
    public async Task StartAsync_updates_mutable_fields_but_preserves_created_at()
    {
        DateTimeOffset originalCreatedAt = DateTimeOffset.UtcNow.AddDays(-7);

        using (IServiceScope seedScope = this.serviceProvider.CreateScope())
        {
            SeederTestDbContext ctx = seedScope.ServiceProvider.GetRequiredService<SeederTestDbContext>();
            ctx.SlackWorkspaceConfigs.Add(new SlackWorkspaceConfig
            {
                TeamId = "T0PRESERVE",
                WorkspaceName = "old-name",
                BotTokenSecretRef = "env://OLDBOT",
                SigningSecretRef = "env://OLDSIGN",
                DefaultChannelId = "C-OLD",
                AllowedChannelIds = Array.Empty<string>(),
                AllowedUserGroupIds = Array.Empty<string>(),
                Enabled = false,
                CreatedAt = originalCreatedAt,
                UpdatedAt = originalCreatedAt,
            });
            await ctx.SaveChangesAsync();
        }

        await this.CreateSeeder(new SlackWorkspaceSeedEntry
        {
            TeamId = "T0PRESERVE",
            WorkspaceName = "new-name",
            SigningSecretRef = "env://NEWSIGN",
            Enabled = true,
        }).StartAsync(CancellationToken.None);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        SeederTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<SeederTestDbContext>();
        SlackWorkspaceConfig? row = await readCtx.SlackWorkspaceConfigs.AsNoTracking()
            .FirstAsync(c => c.TeamId == "T0PRESERVE");

        row.WorkspaceName.Should().Be("new-name", "mutable fields update in-place on re-seed");
        row.SigningSecretRef.Should().Be("env://NEWSIGN");
        row.Enabled.Should().BeTrue();
        row.CreatedAt.Should().BeCloseTo(originalCreatedAt, TimeSpan.FromMilliseconds(1),
            "the seeder must preserve created_at on updates so audit queries that pivot on row age stay accurate");
    }

    [Fact]
    public async Task StartAsync_leaves_unmanaged_rows_alone()
    {
        // A workspace that exists only in the DB (no matching config
        // entry) MUST survive the seed. This lets operators manage
        // extra workspaces out-of-band without the seeder stomping them.
        using (IServiceScope scope = this.serviceProvider.CreateScope())
        {
            SeederTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SeederTestDbContext>();
            ctx.SlackWorkspaceConfigs.Add(new SlackWorkspaceConfig
            {
                TeamId = "T0OUTOFBAND",
                WorkspaceName = "operator-managed",
                BotTokenSecretRef = "env://OOB_BOT",
                SigningSecretRef = "env://OOB_SIGN",
                DefaultChannelId = "C-OOB",
                AllowedChannelIds = Array.Empty<string>(),
                AllowedUserGroupIds = Array.Empty<string>(),
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await this.CreateSeeder(new SlackWorkspaceSeedEntry
        {
            TeamId = "T0FROMCFG",
            SigningSecretRef = "env://CFG",
            Enabled = true,
        }).StartAsync(CancellationToken.None);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        SeederTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<SeederTestDbContext>();
        int total = await readCtx.SlackWorkspaceConfigs.CountAsync();
        total.Should().Be(2, "the out-of-band workspace must coexist with the seeded one");

        bool stillThere = await readCtx.SlackWorkspaceConfigs
            .AnyAsync(c => c.TeamId == "T0OUTOFBAND");
        stillThere.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_no_op_when_no_config_entries()
    {
        SlackWorkspaceConfigSeedHostedService<SeederTestDbContext> seeder = this.CreateSeeder(/* none */);
        await seeder.StartAsync(CancellationToken.None);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        SeederTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<SeederTestDbContext>();
        int total = await readCtx.SlackWorkspaceConfigs.CountAsync();
        total.Should().Be(0);
    }

    private SlackWorkspaceConfigSeedHostedService<SeederTestDbContext> CreateSeeder(
        params SlackWorkspaceSeedEntry[] entries)
    {
        SlackWorkspaceSeedOptions options = new() { Entries = entries };
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new SlackWorkspaceConfigSeedHostedService<SeederTestDbContext>(
            scopeFactory,
            Options.Create(options),
            NullLogger<SlackWorkspaceConfigSeedHostedService<SeederTestDbContext>>.Instance);
    }

    private sealed class SeederTestDbContext : DbContext, ISlackWorkspaceConfigDbContext
    {
        public SeederTestDbContext(DbContextOptions<SeederTestDbContext> options)
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
