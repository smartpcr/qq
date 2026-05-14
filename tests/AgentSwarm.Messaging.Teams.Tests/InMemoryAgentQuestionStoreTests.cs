using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Storage;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class InMemoryAgentQuestionStoreTests
{
    private static AgentQuestion MakeQuestion(string id = "q1", string conversationId = "conv-1", string status = AgentQuestionStatuses.Open)
        => new()
        {
            QuestionId = id,
            AgentId = "agent",
            TaskId = "task",
            TenantId = "tenant",
            TargetUserId = "user",
            Title = "title",
            Body = "body",
            Severity = MessageSeverities.Info,
            AllowedActions = new[] { new HumanAction("a1", "Approve", "approve", false) },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            ConversationId = conversationId,
            CorrelationId = "corr",
            Status = status,
        };

    [Fact]
    public async Task Save_Stamps_CreatedAt_When_Default()
    {
        var store = new InMemoryAgentQuestionStore();
        var q = MakeQuestion();
        await store.SaveAsync(q, default);

        var got = await store.GetByIdAsync("q1", default);
        Assert.NotNull(got);
        Assert.NotEqual(default, got!.CreatedAt);
    }

    [Fact]
    public async Task TryUpdateStatus_Succeeds_From_Expected_State()
    {
        var store = new InMemoryAgentQuestionStore();
        await store.SaveAsync(MakeQuestion(), default);

        var ok = await store.TryUpdateStatusAsync("q1", AgentQuestionStatuses.Open, AgentQuestionStatuses.Resolved, default);
        Assert.True(ok);

        var got = await store.GetByIdAsync("q1", default);
        Assert.Equal(AgentQuestionStatuses.Resolved, got!.Status);
    }

    [Fact]
    public async Task TryUpdateStatus_Fails_When_Expected_Mismatch()
    {
        var store = new InMemoryAgentQuestionStore();
        await store.SaveAsync(MakeQuestion(status: AgentQuestionStatuses.Resolved), default);

        var ok = await store.TryUpdateStatusAsync("q1", AgentQuestionStatuses.Open, AgentQuestionStatuses.Resolved, default);
        Assert.False(ok);
    }

    [Fact]
    public async Task GetMostRecentOpenByConversation_Orders_Descending_By_CreatedAt()
    {
        var store = new InMemoryAgentQuestionStore();
        await store.SaveAsync(MakeQuestion("q1"), default);
        await Task.Delay(5);
        await store.SaveAsync(MakeQuestion("q2"), default);

        var got = await store.GetMostRecentOpenByConversationAsync("conv-1", default);
        Assert.Equal("q2", got!.QuestionId);
    }

    [Fact]
    public async Task GetOpenExpired_Returns_Only_Open_And_Expired()
    {
        var store = new InMemoryAgentQuestionStore();
        var expired = MakeQuestion("q-exp") with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var fresh = MakeQuestion("q-fresh");
        var resolved = MakeQuestion("q-res", status: AgentQuestionStatuses.Resolved) with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await store.SaveAsync(expired, default);
        await store.SaveAsync(fresh, default);
        await store.SaveAsync(resolved, default);

        var found = await store.GetOpenExpiredAsync(DateTimeOffset.UtcNow, 10, default);
        Assert.Single(found);
        Assert.Equal("q-exp", found[0].QuestionId);
    }

    [Fact]
    public async Task UpdateConversationId_Updates_Field()
    {
        var store = new InMemoryAgentQuestionStore();
        await store.SaveAsync(MakeQuestion(conversationId: "old"), default);

        await store.UpdateConversationIdAsync("q1", "new", default);
        var got = await store.GetByIdAsync("q1", default);
        Assert.Equal("new", got!.ConversationId);
    }
}
