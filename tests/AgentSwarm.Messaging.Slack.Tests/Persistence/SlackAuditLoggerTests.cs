// -----------------------------------------------------------------------
// <copyright file="SlackAuditLoggerTests.cs" company="Microsoft Corp.">
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
using Xunit;

/// <summary>
/// Stage 7.1 regression tests for <see cref="SlackAuditLogger{TContext}"/>.
/// Covers the acceptance scenarios from
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// LogAsync persists the row with every mandatory field, the explicit
/// ISlackAuditEntryWriter.AppendAsync forwarder routes through the same
/// EF code path, and QueryAsync filters across correlation_id, task_id,
/// agent_id, team_id, channel_id, user_id, direction, outcome, and time
/// range.
/// </summary>
public sealed class SlackAuditLoggerTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public SlackAuditLoggerTests()
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

    private SlackAuditLogger<RetentionTestDbContext> CreateLogger()
    {
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new SlackAuditLogger<RetentionTestDbContext>(scopeFactory);
    }

    [Fact]
    public async Task LogAsync_persists_slash_command_audit_entry_with_required_fields()
    {
        // AC: "Audit entry persisted on command -- Given a valid slash
        // command processed by SlackCommandHandler, When processing
        // completes, Then a SlackAuditEntry with direction = inbound,
        // request_type = slash_command, and outcome = success is
        // persisted with all mandatory fields populated."
        SlackAuditLogger<RetentionTestDbContext> logger = this.CreateLogger();

        SlackAuditEntry entry = new()
        {
            Id = "01HZAUDIT0000000000000001",
            CorrelationId = "corr-cmd-1",
            TaskId = "task-1",
            AgentId = "agent-1",
            ConversationId = "C0123:thread-99",
            Direction = "inbound",
            RequestType = "slash_command",
            TeamId = "T0123ABCD",
            ChannelId = "C0123ABCD",
            ThreadTs = "1700000000.000100",
            MessageTs = "1700000000.000200",
            UserId = "U0123ABCD",
            CommandText = "/agent ask generate implementation plan for persistence failover",
            ResponsePayload = null,
            Outcome = "success",
            ErrorDetail = null,
            Timestamp = DateTimeOffset.UtcNow,
        };

        await logger.LogAsync(entry, CancellationToken.None);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        RetentionTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        SlackAuditEntry[] rows = await readCtx.SlackAuditEntries.AsNoTracking().ToArrayAsync();

        rows.Should().HaveCount(1);
        SlackAuditEntry row = rows[0];
        row.Direction.Should().Be("inbound");
        row.RequestType.Should().Be("slash_command");
        row.Outcome.Should().Be("success");
        row.TeamId.Should().Be("T0123ABCD");
        row.ChannelId.Should().Be("C0123ABCD");
        row.ThreadTs.Should().Be("1700000000.000100");
        row.UserId.Should().Be("U0123ABCD");
        row.CommandText.Should().Be("/agent ask generate implementation plan for persistence failover");
        row.CorrelationId.Should().Be("corr-cmd-1");
        row.TaskId.Should().Be("task-1");
        row.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public async Task ISlackAuditEntryWriter_AppendAsync_forwards_to_LogAsync()
    {
        // Stage 3.1 callers depend on ISlackAuditEntryWriter; the
        // Stage 7.1 logger MUST satisfy that contract so the wiring
        // swap in AddSlackAuditLogger automatically re-routes
        // signature, authz, idempotency, command, interaction, modal,
        // outbound, and thread audit writes through LogAsync.
        SlackAuditLogger<RetentionTestDbContext> logger = this.CreateLogger();
        ISlackAuditEntryWriter writer = logger;

        SlackAuditEntry entry = MakeEntry("corr-writer", "outbound", "success");

        await writer.AppendAsync(entry, CancellationToken.None);

        using IServiceScope readScope = this.serviceProvider.CreateScope();
        RetentionTestDbContext readCtx = readScope.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        SlackAuditEntry[] rows = await readCtx.SlackAuditEntries.AsNoTracking().ToArrayAsync();
        rows.Should().HaveCount(1);
        rows[0].CorrelationId.Should().Be("corr-writer");
        rows[0].Direction.Should().Be("outbound");
    }

    [Fact]
    public async Task QueryAsync_filters_by_correlation_id_returns_only_matching_rows()
    {
        // AC: "Query by correlation ID -- Given 10 audit entries with
        // varying correlation IDs, When QueryAsync filters by a
        // specific CorrelationId, Then only entries with that ID are
        // returned."
        SlackAuditLogger<RetentionTestDbContext> logger = this.CreateLogger();
        DateTimeOffset basis = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 10; i++)
        {
            SlackAuditEntry e = MakeEntry(
                correlationId: $"corr-{i:00}",
                direction: i % 2 == 0 ? "inbound" : "outbound",
                outcome: "success");
            e.Id = $"01HZQUERY{i:0000000000000000000}";
            e.Timestamp = basis.AddMinutes(i);
            await logger.LogAsync(e, CancellationToken.None);
        }

        SlackAuditQuery query = new() { CorrelationId = "corr-05" };
        var result = await logger.QueryAsync(query, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].CorrelationId.Should().Be("corr-05");
    }

    [Fact]
    public async Task QueryAsync_filters_by_team_channel_user_direction_outcome_and_time_range()
    {
        SlackAuditLogger<RetentionTestDbContext> logger = this.CreateLogger();
        DateTimeOffset basis = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        SlackAuditEntry matching = MakeEntry("corr-match", "inbound", "success");
        matching.Id = "01HZRANGEMATCH00000000000A";
        matching.TeamId = "T-A";
        matching.ChannelId = "C-A";
        matching.UserId = "U-A";
        matching.TaskId = "task-A";
        matching.AgentId = "agent-A";
        matching.Timestamp = basis.AddHours(5);

        SlackAuditEntry wrongTeam = MakeEntry("corr-wt", "inbound", "success");
        wrongTeam.Id = "01HZRANGEMATCH00000000000B";
        wrongTeam.TeamId = "T-B";
        wrongTeam.ChannelId = "C-A";
        wrongTeam.UserId = "U-A";
        wrongTeam.Timestamp = basis.AddHours(5);

        SlackAuditEntry wrongOutcome = MakeEntry("corr-wo", "inbound", "error");
        wrongOutcome.Id = "01HZRANGEMATCH00000000000C";
        wrongOutcome.TeamId = "T-A";
        wrongOutcome.ChannelId = "C-A";
        wrongOutcome.UserId = "U-A";
        wrongOutcome.Timestamp = basis.AddHours(5);

        SlackAuditEntry outOfRange = MakeEntry("corr-oor", "inbound", "success");
        outOfRange.Id = "01HZRANGEMATCH00000000000D";
        outOfRange.TeamId = "T-A";
        outOfRange.ChannelId = "C-A";
        outOfRange.UserId = "U-A";
        outOfRange.Timestamp = basis.AddDays(2);

        foreach (SlackAuditEntry e in new[] { matching, wrongTeam, wrongOutcome, outOfRange })
        {
            await logger.LogAsync(e, CancellationToken.None);
        }

        SlackAuditQuery query = new()
        {
            TeamId = "T-A",
            ChannelId = "C-A",
            UserId = "U-A",
            Direction = "inbound",
            Outcome = "success",
            FromTimestamp = basis,
            ToTimestamp = basis.AddDays(1),
        };

        var result = await logger.QueryAsync(query, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].CorrelationId.Should().Be("corr-match");
    }

    [Fact]
    public async Task QueryAsync_filters_by_task_and_agent_id()
    {
        SlackAuditLogger<RetentionTestDbContext> logger = this.CreateLogger();
        DateTimeOffset basis = DateTimeOffset.UtcNow;

        SlackAuditEntry e1 = MakeEntry("c1", "inbound", "success");
        e1.Id = "01HZTASKAG00000000000001";
        e1.TaskId = "task-1";
        e1.AgentId = "agent-1";
        e1.Timestamp = basis;

        SlackAuditEntry e2 = MakeEntry("c2", "inbound", "success");
        e2.Id = "01HZTASKAG00000000000002";
        e2.TaskId = "task-2";
        e2.AgentId = "agent-1";
        e2.Timestamp = basis;

        SlackAuditEntry e3 = MakeEntry("c3", "inbound", "success");
        e3.Id = "01HZTASKAG00000000000003";
        e3.TaskId = "task-1";
        e3.AgentId = "agent-2";
        e3.Timestamp = basis;

        await logger.LogAsync(e1, CancellationToken.None);
        await logger.LogAsync(e2, CancellationToken.None);
        await logger.LogAsync(e3, CancellationToken.None);

        var task1 = await logger.QueryAsync(new SlackAuditQuery { TaskId = "task-1" }, CancellationToken.None);
        task1.Select(r => r.CorrelationId).Should().BeEquivalentTo(new[] { "c1", "c3" });

        var agent1 = await logger.QueryAsync(new SlackAuditQuery { AgentId = "agent-1" }, CancellationToken.None);
        agent1.Select(r => r.CorrelationId).Should().BeEquivalentTo(new[] { "c1", "c2" });

        var both = await logger.QueryAsync(
            new SlackAuditQuery { TaskId = "task-1", AgentId = "agent-1" },
            CancellationToken.None);
        both.Should().ContainSingle().Which.CorrelationId.Should().Be("c1");
    }

    [Fact]
    public async Task QueryAsync_respects_limit_and_orders_by_timestamp_ascending()
    {
        SlackAuditLogger<RetentionTestDbContext> logger = this.CreateLogger();
        DateTimeOffset basis = new(2024, 9, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 4; i >= 0; i--)
        {
            SlackAuditEntry e = MakeEntry($"c-{i}", "inbound", "success");
            e.Id = $"01HZLIMITS{i:0000000000000000}";
            e.Timestamp = basis.AddMinutes(i);
            await logger.LogAsync(e, CancellationToken.None);
        }

        var result = await logger.QueryAsync(new SlackAuditQuery { Limit = 3 }, CancellationToken.None);
        result.Should().HaveCount(3);
        result.Select(r => r.CorrelationId).Should().ContainInOrder("c-0", "c-1", "c-2");
    }

    [Fact]
    public async Task QueryAsync_throws_for_null_query()
    {
        SlackAuditLogger<RetentionTestDbContext> logger = this.CreateLogger();
        Func<Task> act = async () => await logger.QueryAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task LogAsync_throws_for_null_entry()
    {
        SlackAuditLogger<RetentionTestDbContext> logger = this.CreateLogger();
        Func<Task> act = async () => await logger.LogAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static SlackAuditEntry MakeEntry(string correlationId, string direction, string outcome) => new()
    {
        Id = Guid.NewGuid().ToString("N").ToUpperInvariant().Substring(0, 26),
        CorrelationId = correlationId,
        Direction = direction,
        RequestType = "slash_command",
        TeamId = "T0123ABCD",
        Outcome = outcome,
        Timestamp = DateTimeOffset.UtcNow,
    };
}
