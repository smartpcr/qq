// -----------------------------------------------------------------------
// <copyright file="SlackRetentionCleanupServiceTests.cs" company="Microsoft Corp.">
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
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 7.1 regression tests for
/// <see cref="SlackRetentionCleanupService{TContext}"/>. Covers the
/// retention purge acceptance scenario: rows older than the
/// configured retention window are deleted; rows inside the window
/// are retained. Also asserts that <see cref="SlackInboundRequestRecord"/>
/// rows are purged in lock-step with <see cref="SlackAuditEntry"/>
/// rows (per tech-spec.md §2.7).
/// </summary>
public sealed class SlackRetentionCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public SlackRetentionCleanupServiceTests()
    {
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        ServiceCollection services = new();
        services.AddDbContext<RetentionTestDbContext>(opts => opts.UseSqlite(this.connection));
        this.serviceProvider = services.BuildServiceProvider();

        using IServiceScope bootstrap = this.serviceProvider.CreateScope();
        RetentionTestDbContext ctx = bootstrap.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        this.serviceProvider.Dispose();
        this.connection.Dispose();
    }

    private SlackRetentionCleanupService<RetentionTestDbContext> CreateService(
        SlackRetentionOptions opts,
        TimeProvider? clock = null)
    {
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        IOptionsMonitor<SlackRetentionOptions> monitor = new TestOptionsMonitor(opts);
        return new SlackRetentionCleanupService<RetentionTestDbContext>(
            scopeFactory,
            monitor,
            NullLogger<SlackRetentionCleanupService<RetentionTestDbContext>>.Instance,
            clock ?? TimeProvider.System);
    }

    [Fact]
    public async Task RunOnceAsync_purges_audit_entries_older_than_30_days_and_keeps_newer_rows()
    {
        // AC: "Retention cleanup purges old records -- Given audit
        // entries with Timestamp older than 30 days, When
        // SlackRetentionCleanupService runs, Then those entries are
        // deleted and entries newer than 30 days are retained."
        DateTimeOffset now = new(2025, 5, 1, 12, 0, 0, TimeSpan.Zero);
        SlackAuditEntry oldRow = MakeAuditEntry("old", now - TimeSpan.FromDays(45));
        SlackAuditEntry edgeOld = MakeAuditEntry("edge-old", now - TimeSpan.FromDays(31));
        SlackAuditEntry fresh = MakeAuditEntry("fresh", now - TimeSpan.FromDays(5));
        SlackAuditEntry brandNew = MakeAuditEntry("brand-new", now);

        await this.SeedAuditEntriesAsync(oldRow, edgeOld, fresh, brandNew);

        SlackRetentionCleanupService<RetentionTestDbContext> service = this.CreateService(
            new SlackRetentionOptions { RetentionDays = 30, BatchSize = 100 });

        SlackRetentionSweepResult result = await service.RunOnceAsync(now, CancellationToken.None);

        result.AuditEntriesDeleted.Should().Be(2);
        result.InboundRequestsDeleted.Should().Be(0);
        result.Cutoff.Should().Be(now - TimeSpan.FromDays(30));

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        RetentionTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        SlackAuditEntry[] rows = await readCtx.SlackAuditEntries.AsNoTracking().ToArrayAsync();
        rows.Select(r => r.Id).Should().BeEquivalentTo(new[] { fresh.Id, brandNew.Id });
    }

    [Fact]
    public async Task RunOnceAsync_purges_inbound_request_records_older_than_retention()
    {
        DateTimeOffset now = new(2025, 5, 1, 12, 0, 0, TimeSpan.Zero);
        SlackInboundRequestRecord oldR = MakeInboundRequest("idem-old", now - TimeSpan.FromDays(60));
        SlackInboundRequestRecord borderlineOld = MakeInboundRequest("idem-edge", now - TimeSpan.FromDays(31));
        SlackInboundRequestRecord recent = MakeInboundRequest("idem-recent", now - TimeSpan.FromDays(10));

        await this.SeedInboundRequestsAsync(oldR, borderlineOld, recent);

        SlackRetentionCleanupService<RetentionTestDbContext> service = this.CreateService(
            new SlackRetentionOptions { RetentionDays = 30, BatchSize = 100 });

        SlackRetentionSweepResult result = await service.RunOnceAsync(now, CancellationToken.None);

        result.InboundRequestsDeleted.Should().Be(2);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        RetentionTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        SlackInboundRequestRecord[] rows = await readCtx.SlackInboundRequestRecords.AsNoTracking().ToArrayAsync();
        rows.Select(r => r.IdempotencyKey).Should().BeEquivalentTo(new[] { "idem-recent" });
    }

    [Fact]
    public async Task RunOnceAsync_handles_batching_when_more_rows_than_batch_size_are_eligible()
    {
        DateTimeOffset now = new(2025, 5, 1, 12, 0, 0, TimeSpan.Zero);
        SlackAuditEntry[] aged = Enumerable.Range(0, 25)
            .Select(i => MakeAuditEntry($"batched-{i:00}", now - TimeSpan.FromDays(40 + i)))
            .ToArray();
        await this.SeedAuditEntriesAsync(aged);

        SlackRetentionCleanupService<RetentionTestDbContext> service = this.CreateService(
            new SlackRetentionOptions { RetentionDays = 30, BatchSize = 5 });

        SlackRetentionSweepResult result = await service.RunOnceAsync(now, CancellationToken.None);

        result.AuditEntriesDeleted.Should().Be(25);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        RetentionTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        (await readCtx.SlackAuditEntries.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunOnceAsync_uses_default_retention_when_options_value_invalid()
    {
        DateTimeOffset now = new(2025, 5, 1, 12, 0, 0, TimeSpan.Zero);
        SlackAuditEntry recent = MakeAuditEntry("recent", now - TimeSpan.FromDays(5));
        SlackAuditEntry expired = MakeAuditEntry("expired", now - TimeSpan.FromDays(45));
        await this.SeedAuditEntriesAsync(recent, expired);

        SlackRetentionCleanupService<RetentionTestDbContext> service = this.CreateService(
            new SlackRetentionOptions { RetentionDays = 0, BatchSize = 0 });

        SlackRetentionSweepResult result = await service.RunOnceAsync(now, CancellationToken.None);
        result.Cutoff.Should().Be(now - TimeSpan.FromDays(SlackRetentionOptions.DefaultRetentionDays));
        result.AuditEntriesDeleted.Should().Be(1);
    }

    private async Task SeedAuditEntriesAsync(params SlackAuditEntry[] entries)
    {
        using IServiceScope scope = this.serviceProvider.CreateScope();
        RetentionTestDbContext ctx = scope.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        ctx.SlackAuditEntries.AddRange(entries);
        await ((ISlackAuditEntryDbContext)ctx).SaveChangesAsync(CancellationToken.None);
    }

    private async Task SeedInboundRequestsAsync(params SlackInboundRequestRecord[] records)
    {
        using IServiceScope scope = this.serviceProvider.CreateScope();
        RetentionTestDbContext ctx = scope.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        ctx.SlackInboundRequestRecords.AddRange(records);
        await ((ISlackInboundRequestRecordDbContext)ctx).SaveChangesAsync(CancellationToken.None);
    }

    private static SlackAuditEntry MakeAuditEntry(string suffix, DateTimeOffset timestamp) => new()
    {
        Id = $"01HZ-{suffix}",
        CorrelationId = $"corr-{suffix}",
        Direction = "inbound",
        RequestType = "slash_command",
        TeamId = "T0123ABCD",
        Outcome = "success",
        Timestamp = timestamp,
    };

    private static SlackInboundRequestRecord MakeInboundRequest(string idem, DateTimeOffset firstSeen) => new()
    {
        IdempotencyKey = idem,
        SourceType = "command",
        TeamId = "T0123ABCD",
        ChannelId = "C-A",
        UserId = "U-A",
        RawPayloadHash = "0000000000000000000000000000000000000000000000000000000000000000",
        ProcessingStatus = "completed",
        FirstSeenAt = firstSeen,
        CompletedAt = firstSeen,
    };

    private sealed class TestOptionsMonitor : IOptionsMonitor<SlackRetentionOptions>
    {
        private readonly SlackRetentionOptions value;

        public TestOptionsMonitor(SlackRetentionOptions value)
        {
            this.value = value;
        }

        public SlackRetentionOptions CurrentValue => this.value;

        public SlackRetentionOptions Get(string? name) => this.value;

        public IDisposable OnChange(Action<SlackRetentionOptions, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
