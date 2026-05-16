using AgentSwarm.Messaging.Persistence;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Validates the three canonical compliance-review queries on
/// <see cref="SqlAuditLogQueryService"/> per <c>implementation-plan.md</c> §5.2 step 6.
/// </summary>
public sealed class SqlAuditLogQueryServiceTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 5, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetByDateRangeAsync_ReturnsOnlyEntriesWithinHalfOpenWindow()
    {
        await using var fixture = new AuditLogStoreFixture();

        await SeedAsync(fixture, T0.AddMinutes(-1), "corr-before", actor: "user-A");
        await SeedAsync(fixture, T0, "corr-start", actor: "user-A");
        await SeedAsync(fixture, T0.AddMinutes(30), "corr-mid", actor: "user-B");
        await SeedAsync(fixture, T0.AddMinutes(60), "corr-end", actor: "user-B");
        await SeedAsync(fixture, T0.AddMinutes(90), "corr-after", actor: "user-A");

        var rows = await fixture.QueryService.GetByDateRangeAsync(
            T0, T0.AddMinutes(60), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal("corr-start", rows[0].CorrelationId);
        Assert.Equal("corr-mid", rows[1].CorrelationId);
    }

    [Fact]
    public async Task GetByDateRangeAsync_OrdersByTimestampAscending()
    {
        await using var fixture = new AuditLogStoreFixture();

        // Insert in reverse chronological order to prove the query re-orders.
        await SeedAsync(fixture, T0.AddMinutes(30), "corr-c", actor: "user-A");
        await SeedAsync(fixture, T0.AddMinutes(10), "corr-a", actor: "user-A");
        await SeedAsync(fixture, T0.AddMinutes(20), "corr-b", actor: "user-A");

        var rows = await fixture.QueryService.GetByDateRangeAsync(
            T0, T0.AddHours(1), CancellationToken.None);

        Assert.Equal(new[] { "corr-a", "corr-b", "corr-c" },
            rows.Select(r => r.CorrelationId).ToArray());
    }

    [Fact]
    public async Task GetByDateRangeAsync_InvalidWindow_Throws()
    {
        await using var fixture = new AuditLogStoreFixture();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.QueryService.GetByDateRangeAsync(T0.AddHours(1), T0, CancellationToken.None));

        // Equal bounds are also rejected — the query is documented as half-open
        // [from, to) which requires to > from.
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.QueryService.GetByDateRangeAsync(T0, T0, CancellationToken.None));
    }

    [Fact]
    public async Task GetByActorAsync_FiltersByExactActorIdAndOrdersChronologically()
    {
        await using var fixture = new AuditLogStoreFixture();

        await SeedAsync(fixture, T0, "corr-1", actor: "user-A");
        await SeedAsync(fixture, T0.AddMinutes(5), "corr-2", actor: "user-B");
        await SeedAsync(fixture, T0.AddMinutes(10), "corr-3", actor: "user-A");

        var rows = await fixture.QueryService.GetByActorAsync("user-A", CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal("corr-1", rows[0].CorrelationId);
        Assert.Equal("corr-3", rows[1].CorrelationId);
    }

    [Fact]
    public async Task GetByActorAsync_IsCaseSensitive()
    {
        await using var fixture = new AuditLogStoreFixture();

        await SeedAsync(fixture, T0, "corr-1", actor: "user-A");

        // The store uses ordinal equality so "USER-A" is a different actor — compliance
        // review expects AAD object IDs to be compared verbatim.
        var rows = await fixture.QueryService.GetByActorAsync("USER-A", CancellationToken.None);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetByActorAsync_EmptyActorId_Throws()
    {
        await using var fixture = new AuditLogStoreFixture();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.QueryService.GetByActorAsync(string.Empty, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.QueryService.GetByActorAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task GetByCorrelationIdAsync_ReturnsAllEntriesForTraceInChronologicalOrder()
    {
        await using var fixture = new AuditLogStoreFixture();

        // A single user command typically produces multiple audit entries
        // (CommandReceived → CardActionReceived → ProactiveNotification) all sharing
        // the same correlation ID — assert they're returned in the order they happened.
        await SeedAsync(fixture, T0.AddSeconds(2), "corr-trace", actor: "user-X");
        await SeedAsync(fixture, T0, "corr-trace", actor: "user-X");
        await SeedAsync(fixture, T0.AddSeconds(1), "corr-trace", actor: "agent-1");
        await SeedAsync(fixture, T0, "corr-other", actor: "user-X");

        var rows = await fixture.QueryService.GetByCorrelationIdAsync(
            "corr-trace", CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(T0, rows[0].Timestamp);
        Assert.Equal(T0.AddSeconds(1), rows[1].Timestamp);
        Assert.Equal(T0.AddSeconds(2), rows[2].Timestamp);
    }

    [Fact]
    public async Task GetByCorrelationIdAsync_EmptyId_Throws()
    {
        await using var fixture = new AuditLogStoreFixture();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.QueryService.GetByCorrelationIdAsync(string.Empty, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.QueryService.GetByCorrelationIdAsync("\t", CancellationToken.None));
    }

    [Fact]
    public async Task ConstructedQueries_PreserveStoredChecksum()
    {
        await using var fixture = new AuditLogStoreFixture();

        var ts = T0.AddMinutes(5);
        var corr = "corr-checksum";
        await SeedAsync(fixture, ts, corr, actor: "user-A");

        var byCorr = await fixture.QueryService.GetByCorrelationIdAsync(corr, CancellationToken.None);
        var single = Assert.Single(byCorr);

        // Re-compute the checksum from the projected fields and verify it matches.
        var recomputed = AuditEntry.ComputeChecksum(
            timestamp: single.Timestamp,
            correlationId: single.CorrelationId,
            eventType: single.EventType,
            actorId: single.ActorId,
            actorType: single.ActorType,
            tenantId: single.TenantId,
            agentId: single.AgentId,
            taskId: single.TaskId,
            conversationId: single.ConversationId,
            action: single.Action,
            payloadJson: single.PayloadJson,
            outcome: single.Outcome);

        Assert.Equal(recomputed, single.Checksum);
    }

    private static async Task SeedAsync(
        AuditLogStoreFixture fixture,
        DateTimeOffset timestamp,
        string correlationId,
        string actor)
    {
        const string eventType = AuditEventTypes.CommandReceived;
        const string actorType = AuditActorTypes.User;
        const string tenantId = "tenant-1";
        const string agentId = "agent-1";
        const string taskId = "task-1";
        const string conversationId = "conv-1";
        const string action = "agent ask";
        const string payloadJson = "{\"command\":\"agent ask\"}";
        const string outcome = AuditOutcomes.Success;

        var checksum = AuditEntry.ComputeChecksum(
            timestamp: timestamp,
            correlationId: correlationId,
            eventType: eventType,
            actorId: actor,
            actorType: actorType,
            tenantId: tenantId,
            agentId: agentId,
            taskId: taskId,
            conversationId: conversationId,
            action: action,
            payloadJson: payloadJson,
            outcome: outcome);

        var entry = new AuditEntry
        {
            Timestamp = timestamp,
            CorrelationId = correlationId,
            EventType = eventType,
            ActorId = actor,
            ActorType = actorType,
            TenantId = tenantId,
            AgentId = agentId,
            TaskId = taskId,
            ConversationId = conversationId,
            Action = action,
            PayloadJson = payloadJson,
            Outcome = outcome,
            Checksum = checksum,
        };

        await fixture.Logger.LogAsync(entry, CancellationToken.None);
    }
}
