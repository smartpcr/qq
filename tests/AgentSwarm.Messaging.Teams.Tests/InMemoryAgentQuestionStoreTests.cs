using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class InMemoryAgentQuestionStoreTests
{
    [Fact]
    public async Task SaveAsync_StampsCreatedAt_WhenDefault()
    {
        var store = new InMemoryAgentQuestionStore();
        var question = MakeOpen("q-1") with { CreatedAt = default };

        await store.SaveAsync(question, default);
        var fetched = await store.GetByIdAsync("q-1", default);

        Assert.NotNull(fetched);
        Assert.NotEqual(default, fetched!.CreatedAt);
    }

    [Fact]
    public async Task TryUpdateStatusAsync_TransitionsWhenExpectedMatches()
    {
        var store = new InMemoryAgentQuestionStore();
        await store.SaveAsync(MakeOpen("q-2"), default);

        var ok = await store.TryUpdateStatusAsync("q-2", AgentQuestionStatuses.Open, AgentQuestionStatuses.Resolved, default);

        Assert.True(ok);
        var fetched = await store.GetByIdAsync("q-2", default);
        Assert.Equal(AgentQuestionStatuses.Resolved, fetched!.Status);
    }

    [Fact]
    public async Task TryUpdateStatusAsync_ReturnsFalse_OnStatusMismatch()
    {
        var store = new InMemoryAgentQuestionStore();
        await store.SaveAsync(MakeOpen("q-3"), default);
        await store.TryUpdateStatusAsync("q-3", AgentQuestionStatuses.Open, AgentQuestionStatuses.Resolved, default);

        var second = await store.TryUpdateStatusAsync("q-3", AgentQuestionStatuses.Open, AgentQuestionStatuses.Expired, default);

        Assert.False(second);
    }

    [Fact]
    public async Task GetMostRecentOpenByConversation_ReturnsLatest()
    {
        var store = new InMemoryAgentQuestionStore();
        var older = MakeOpen("q-4") with { ConversationId = "conv-x", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var newer = MakeOpen("q-5") with { ConversationId = "conv-x", CreatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(older, default);
        await store.SaveAsync(newer, default);

        var result = await store.GetMostRecentOpenByConversationAsync("conv-x", default);

        Assert.NotNull(result);
        Assert.Equal("q-5", result!.QuestionId);
    }

    [Fact]
    public async Task GetOpenByConversation_ReturnsAllOpenOrdered()
    {
        var store = new InMemoryAgentQuestionStore();
        await store.SaveAsync(MakeOpen("q-6") with { ConversationId = "c", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3) }, default);
        await store.SaveAsync(MakeOpen("q-7") with { ConversationId = "c", CreatedAt = DateTimeOffset.UtcNow }, default);
        await store.SaveAsync(MakeOpen("q-8") with { ConversationId = "c", Status = AgentQuestionStatuses.Resolved }, default);

        var list = await store.GetOpenByConversationAsync("c", default);

        Assert.Equal(2, list.Count);
        Assert.Equal("q-7", list[0].QuestionId);
        Assert.Equal("q-6", list[1].QuestionId);
    }

    [Fact]
    public async Task GetOpenExpired_ReturnsExpiredOpenLimitedToBatchSize()
    {
        var store = new InMemoryAgentQuestionStore();
        var past = DateTimeOffset.UtcNow.AddMinutes(-10);
        var future = DateTimeOffset.UtcNow.AddMinutes(10);
        await store.SaveAsync(MakeOpen("q-9") with { ExpiresAt = past }, default);
        await store.SaveAsync(MakeOpen("q-10") with { ExpiresAt = past.AddMinutes(-1) }, default);
        await store.SaveAsync(MakeOpen("q-11") with { ExpiresAt = future }, default);
        await store.SaveAsync(MakeOpen("q-12") with { ExpiresAt = past, Status = AgentQuestionStatuses.Resolved }, default);

        var list = await store.GetOpenExpiredAsync(DateTimeOffset.UtcNow, batchSize: 1, default);

        Assert.Single(list);
        // The earlier-expiring (older ExpiresAt) one comes first.
        Assert.Equal("q-10", list[0].QuestionId);
    }

    [Fact]
    public async Task UpdateConversationIdAsync_PersistsValue()
    {
        var store = new InMemoryAgentQuestionStore();
        await store.SaveAsync(MakeOpen("q-13"), default);

        await store.UpdateConversationIdAsync("q-13", "conv-new", default);
        var fetched = await store.GetByIdAsync("q-13", default);

        Assert.Equal("conv-new", fetched!.ConversationId);
    }

    private static AgentQuestion MakeOpen(string id) => new()
    {
        QuestionId = id,
        AgentId = "agent-1",
        TaskId = "task-1",
        TenantId = "tenant-a",
        TargetUserId = "u-1",
        Title = "Title",
        Body = "Body",
        Severity = MessageSeverities.Info,
        AllowedActions = new[]
        {
            new HumanAction("approve", "Approve", "approve", false),
        },
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        CorrelationId = "corr-" + id,
        CreatedAt = DateTimeOffset.UtcNow,
        Status = AgentQuestionStatuses.Open,
    };
}
