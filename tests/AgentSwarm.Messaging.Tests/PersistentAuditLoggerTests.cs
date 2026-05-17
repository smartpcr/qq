using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 iter-2 evaluator item 5 — round-trip tests for
/// <see cref="PersistentAuditLogger"/> against an in-memory SQLite
/// connection using the real <see cref="MessagingDbContext"/> schema
/// (so the <see cref="AuditLogEntryConfiguration"/>-defined indexes
/// and the <see cref="DateTimeOffset"/> Unix-ms value converter both
/// run end-to-end).
/// </summary>
public sealed class PersistentAuditLoggerTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private PersistentAuditLogger _logger = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        await using (var scope = _provider.CreateAsyncScope())
        await using (var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _logger = new PersistentAuditLogger(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PersistentAuditLogger>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task LogAsync_PersistsRowWithGeneralEntryKind()
    {
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "msg-1",
            UserId = "u-1",
            AgentId = "agent-7",
            Action = "command.received",
            Timestamp = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            CorrelationId = "trace-1",
            Details = "{\"raw\":\"/status\"}",
        };

        await _logger.LogAsync(entry, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await ctx.AuditLogEntries.SingleAsync();
        row.EntryId.Should().Be(entry.EntryId);
        row.EntryKind.Should().Be(AuditEntryKind.General);
        row.MessageId.Should().Be("msg-1");
        row.UserId.Should().Be("u-1");
        row.AgentId.Should().Be("agent-7");
        row.Action.Should().Be("command.received");
        row.Timestamp.Should().Be(entry.Timestamp);
        row.CorrelationId.Should().Be("trace-1");
        row.Details.Should().Be("{\"raw\":\"/status\"}");
        row.QuestionId.Should().BeNull();
        row.ActionValue.Should().BeNull();
        row.Comment.Should().BeNull();
    }

    [Fact]
    public async Task LogHumanResponseAsync_PersistsRowWithAllStoryRequiredFields()
    {
        // Story brief: "Persist every human response with message ID,
        // user ID, agent ID, timestamp, and correlation ID." This test
        // pins that every named field round-trips through the writer,
        // and that the EntryKind discriminator separates the row from
        // a general AuditEntry.
        var entry = new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "msg-42",
            UserId = "u-42",
            AgentId = "agent-9",
            QuestionId = "Q-1",
            ActionValue = "approve",
            Comment = "looks good",
            Timestamp = new DateTimeOffset(2025, 2, 1, 9, 30, 0, TimeSpan.Zero),
            CorrelationId = "trace-42",
        };

        await _logger.LogHumanResponseAsync(entry, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await ctx.AuditLogEntries.SingleAsync();
        row.EntryId.Should().Be(entry.EntryId);
        row.EntryKind.Should().Be(AuditEntryKind.HumanResponse);
        row.MessageId.Should().Be("msg-42");
        row.UserId.Should().Be("u-42");
        row.AgentId.Should().Be("agent-9");
        row.QuestionId.Should().Be("Q-1");
        row.ActionValue.Should().Be("approve");
        row.Comment.Should().Be("looks good");
        row.Timestamp.Should().Be(entry.Timestamp);
        row.CorrelationId.Should().Be("trace-42");
    }

    [Fact]
    public async Task LogHumanResponseAsync_DefaultsActionToHumanResponse()
    {
        // The Action column carries the verb shared with the general
        // AuditEntry path so a Stage 5.3 join can filter human responses
        // by `Action = 'human.response'` without consulting EntryKind.
        var entry = new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "m",
            UserId = "u",
            AgentId = "a",
            QuestionId = "q",
            ActionValue = "reject",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-action",
        };

        await _logger.LogHumanResponseAsync(entry, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await ctx.AuditLogEntries.SingleAsync();
        row.Action.Should().Be("human.response");
    }

    [Fact]
    public async Task BothLoggerPaths_PersistToSameTable_DiscriminatedByEntryKind()
    {
        await _logger.LogAsync(new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-1",
            Action = "command.received",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-a",
        }, default);
        await _logger.LogHumanResponseAsync(new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "m",
            UserId = "u-2",
            AgentId = "a",
            QuestionId = "Q-2",
            ActionValue = "approve",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-b",
        }, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var rows = await ctx.AuditLogEntries.OrderBy(x => x.CorrelationId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.EntryKind).Should().BeEquivalentTo(
            new[] { AuditEntryKind.General, AuditEntryKind.HumanResponse });
    }
}
