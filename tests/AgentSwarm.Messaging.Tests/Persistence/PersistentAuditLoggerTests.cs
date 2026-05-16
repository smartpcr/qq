using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Stage 2.2 acceptance tests for <see cref="PersistentAuditLogger"/>.
/// Covers the audit-log Discord IDs scenario from the implementation-plan
/// and the human-response details roll-up.
/// </summary>
public class PersistentAuditLoggerTests : IDisposable
{
    private readonly SqliteContextHarness _harness = new();
    private readonly PersistentAuditLogger _logger;

    public PersistentAuditLoggerTests()
    {
        _logger = new PersistentAuditLogger(_harness.Factory);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task LogAsync_DiscordDetails_PersistsRow()
    {
        // Stage 2.2 Test Scenario: "Audit log stores Discord details --
        // AuditEntry with Platform=Discord and Details JSON containing
        // GuildId and ChannelId is persisted".
        var entry = new AuditEntry(
            Platform: "Discord",
            ExternalUserId: "1234567890",
            MessageId: "9876543210",
            Details: "{\"GuildId\":\"111\",\"ChannelId\":\"222\",\"InteractionId\":\"333\"}",
            Timestamp: new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero),
            CorrelationId: "corr-1");

        await _logger.LogAsync(entry, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var row = await ctx.AuditLog.SingleAsync();
        row.Platform.Should().Be("Discord");
        row.ExternalUserId.Should().Be("1234567890");
        row.MessageId.Should().Be("9876543210");
        row.Details.Should().Contain("\"GuildId\":\"111\"")
            .And.Contain("\"ChannelId\":\"222\"")
            .And.Contain("\"InteractionId\":\"333\"");
        row.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task LogHumanResponseAsync_AugmentsDetailsWithDecisionFields()
    {
        var entry = new HumanResponseAuditEntry(
            Platform: "Discord",
            ExternalUserId: "user-1",
            MessageId: "msg-1",
            QuestionId: "Q-42",
            SelectedActionId: "approve",
            ActionValue: "yes",
            Comment: "looks fine",
            Details: "{\"GuildId\":\"111\",\"ChannelId\":\"222\"}",
            Timestamp: new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero),
            CorrelationId: "corr-h");

        await _logger.LogHumanResponseAsync(entry, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var row = await ctx.AuditLog.SingleAsync();
        using var doc = JsonDocument.Parse(row.Details);
        doc.RootElement.GetProperty("GuildId").GetString().Should().Be("111");
        doc.RootElement.GetProperty("ChannelId").GetString().Should().Be("222");
        doc.RootElement.GetProperty("QuestionId").GetString().Should().Be("Q-42");
        doc.RootElement.GetProperty("SelectedActionId").GetString().Should().Be("approve");
        doc.RootElement.GetProperty("ActionValue").GetString().Should().Be("yes");
        doc.RootElement.GetProperty("Comment").GetString().Should().Be("looks fine");
    }

    [Fact]
    public async Task LogHumanResponseAsync_NullComment_OmitsCommentKey()
    {
        var entry = new HumanResponseAuditEntry(
            Platform: "Discord",
            ExternalUserId: "user-1",
            MessageId: "msg-1",
            QuestionId: "Q-43",
            SelectedActionId: "reject",
            ActionValue: "no",
            Comment: null,
            Details: "{}",
            Timestamp: new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero),
            CorrelationId: "corr-h2");

        await _logger.LogHumanResponseAsync(entry, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var row = await ctx.AuditLog.SingleAsync();
        using var doc = JsonDocument.Parse(row.Details);
        doc.RootElement.TryGetProperty("Comment", out _).Should().BeFalse();
    }

    [Fact]
    public async Task LogAsync_NullEntry_Throws()
    {
        var act = async () => await _logger.LogAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
