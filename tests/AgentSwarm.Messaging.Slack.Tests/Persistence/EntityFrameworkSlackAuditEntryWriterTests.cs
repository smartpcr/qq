// -----------------------------------------------------------------------
// <copyright file="EntityFrameworkSlackAuditEntryWriterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Stage 3.1 regression tests for
/// <see cref="EntityFrameworkSlackAuditEntryWriter{TContext}"/>. The
/// iter-1 review flagged that "no changed file maps
/// SlackSignatureAuditRecord into SlackAuditEntry" -- this fixture
/// exercises the end-to-end EF Core path so the canonical
/// <c>slack_audit_entry</c> insert is covered, not just the in-memory
/// stub.
/// </summary>
public sealed class EntityFrameworkSlackAuditEntryWriterTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public EntityFrameworkSlackAuditEntryWriterTests()
    {
        // SQLite :memory: databases vanish when the last connection
        // closes; keeping an outer connection alive lets every scoped
        // DbContext see the same schema for the duration of the test.
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        ServiceCollection services = new();
        services.AddDbContext<AuditTestDbContext>(opts => opts.UseSqlite(this.connection));
        this.serviceProvider = services.BuildServiceProvider();

        using IServiceScope bootstrap = this.serviceProvider.CreateScope();
        AuditTestDbContext ctx = bootstrap.ServiceProvider.GetRequiredService<AuditTestDbContext>();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        this.serviceProvider.Dispose();
        this.connection.Dispose();
    }

    [Fact]
    public async Task AppendAsync_persists_entry_to_slack_audit_entry_table()
    {
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        EntityFrameworkSlackAuditEntryWriter<AuditTestDbContext> writer = new(scopeFactory);

        SlackAuditEntry entry = new()
        {
            Id = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            CorrelationId = "corr-1",
            Direction = "inbound",
            RequestType = "event",
            TeamId = "T0123ABCD",
            Outcome = SlackSignatureAuditRecord.RejectedSignatureOutcome,
            ErrorDetail = "HMAC mismatch",
            Timestamp = DateTimeOffset.UtcNow,
        };

        await writer.AppendAsync(entry, CancellationToken.None);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        AuditTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
        SlackAuditEntry[] rows = await readCtx.SlackAuditEntries.AsNoTracking().ToArrayAsync();

        rows.Should().HaveCount(1);
        rows[0].Outcome.Should().Be(SlackSignatureAuditRecord.RejectedSignatureOutcome);
        rows[0].TeamId.Should().Be("T0123ABCD");
        rows[0].RequestType.Should().Be("event");
    }

    [Fact]
    public async Task AppendAsync_rejects_null_entry()
    {
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        EntityFrameworkSlackAuditEntryWriter<AuditTestDbContext> writer = new(scopeFactory);

        Func<Task> act = async () => await writer.AppendAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AppendAsync_creates_a_fresh_scope_per_call_so_the_singleton_writer_is_safe()
    {
        // Two sequential appends from the same singleton writer must
        // succeed without state leak (the per-call AsyncServiceScope
        // disposes the DbContext between calls).
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        EntityFrameworkSlackAuditEntryWriter<AuditTestDbContext> writer = new(scopeFactory);

        SlackAuditEntry first = MakeEntry("first");
        SlackAuditEntry second = MakeEntry("second");

        await writer.AppendAsync(first, CancellationToken.None);
        await writer.AppendAsync(second, CancellationToken.None);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        AuditTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
        SlackAuditEntry[] rows = await readCtx.SlackAuditEntries.AsNoTracking()
            .OrderBy(e => e.Id)
            .ToArrayAsync();

        rows.Should().HaveCount(2);
        rows.Select(r => r.CorrelationId).Should().BeEquivalentTo(new[] { "first", "second" });
    }

    private static SlackAuditEntry MakeEntry(string correlation) => new()
    {
        Id = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        CorrelationId = correlation,
        Direction = "inbound",
        RequestType = "event",
        TeamId = "T0123ABCD",
        Outcome = SlackSignatureAuditRecord.RejectedSignatureOutcome,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private sealed class AuditTestDbContext : DbContext, ISlackAuditEntryDbContext
    {
        public AuditTestDbContext(DbContextOptions<AuditTestDbContext> options)
            : base(options)
        {
        }

        public DbSet<SlackAuditEntry> SlackAuditEntries => this.Set<SlackAuditEntry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.AddSlackEntities();
        }
    }
}
