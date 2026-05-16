using AgentSwarm.Messaging.Abstractions;
using Xunit;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

public sealed class SqlAgentQuestionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static AgentQuestion BuildQuestion(
        string questionId = "q-001",
        string? conversationId = null,
        string status = AgentQuestionStatuses.Open,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? createdAt = null,
        IReadOnlyList<HumanAction>? actions = null)
    {
        return new AgentQuestion
        {
            QuestionId = questionId,
            AgentId = "agent-1",
            TaskId = "task-1",
            TenantId = "tenant-aaa",
            TargetUserId = "user-1",
            Title = "Need decision",
            Body = "Approve or reject?",
            Severity = MessageSeverities.Info,
            AllowedActions = actions ?? new[]
            {
                new HumanAction("a1", "Approve", "approve", false),
                new HumanAction("a2", "Reject", "reject", true),
            },
            ExpiresAt = expiresAt ?? Now.AddHours(1),
            CorrelationId = "corr-1",
            ConversationId = conversationId,
            Status = status,
            CreatedAt = createdAt ?? default,
        };
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAllowedActions()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);
        var question = BuildQuestion();

        await fx.QuestionStore.SaveAsync(question, CancellationToken.None);

        var loaded = await fx.QuestionStore.GetByIdAsync(question.QuestionId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.AllowedActions.Count);
        Assert.Equal("approve", loaded.AllowedActions[0].Value);
        Assert.Equal("Approve", loaded.AllowedActions[0].Label);
        Assert.False(loaded.AllowedActions[0].RequiresComment);
        Assert.Equal("reject", loaded.AllowedActions[1].Value);
        Assert.True(loaded.AllowedActions[1].RequiresComment);
    }

    [Fact]
    public async Task SaveAsync_NormalisesCallerStatus_ToOpen()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        // Caller hands us a "Resolved" status — the store must normalise to Open per
        // the iter-2 critique #2 about store-owned creation semantics.
        var question = BuildQuestion(status: AgentQuestionStatuses.Resolved);

        await fx.QuestionStore.SaveAsync(question, CancellationToken.None);

        var loaded = await fx.QuestionStore.GetByIdAsync(question.QuestionId, CancellationToken.None);

        Assert.Equal(AgentQuestionStatuses.Open, loaded!.Status);
    }

    [Fact]
    public async Task SaveAsync_NormalisesCallerCreatedAt_ToNow()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        var stale = Now.AddYears(-5);
        var question = BuildQuestion(createdAt: stale);

        await fx.QuestionStore.SaveAsync(question, CancellationToken.None);

        var loaded = await fx.QuestionStore.GetByIdAsync(question.QuestionId, CancellationToken.None);

        Assert.Equal(Now, loaded!.CreatedAt);
        Assert.NotEqual(stale, loaded.CreatedAt);
    }

    [Fact]
    public async Task TryUpdateStatusAsync_FirstWriterWins()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);
        await fx.QuestionStore.SaveAsync(BuildQuestion(), CancellationToken.None);

        var first = await fx.QuestionStore.TryUpdateStatusAsync(
            "q-001",
            AgentQuestionStatuses.Open,
            AgentQuestionStatuses.Resolved,
            CancellationToken.None);

        var second = await fx.QuestionStore.TryUpdateStatusAsync(
            "q-001",
            AgentQuestionStatuses.Open,
            AgentQuestionStatuses.Resolved,
            CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task TryUpdateStatusAsync_StampsResolvedAt_OnTerminalTransition()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);
        await fx.QuestionStore.SaveAsync(BuildQuestion(), CancellationToken.None);

        // Advance the clock so ResolvedAt is distinguishable from CreatedAt.
        clock.Advance(TimeSpan.FromMinutes(5));

        await fx.QuestionStore.TryUpdateStatusAsync(
            "q-001",
            AgentQuestionStatuses.Open,
            AgentQuestionStatuses.Resolved,
            CancellationToken.None);

        await using var ctx = fx.CreateContext();
        var entity = ctx.AgentQuestions.Single(e => e.QuestionId == "q-001");
        Assert.NotNull(entity.ResolvedAt);
        Assert.Equal(Now.AddMinutes(5), entity.ResolvedAt!.Value);
    }

    [Fact]
    public async Task UpdateConversationIdAsync_Persists()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);
        await fx.QuestionStore.SaveAsync(BuildQuestion(), CancellationToken.None);

        await fx.QuestionStore.UpdateConversationIdAsync("q-001", "conv-XYZ", CancellationToken.None);

        var loaded = await fx.QuestionStore.GetByIdAsync("q-001", CancellationToken.None);
        Assert.Equal("conv-XYZ", loaded!.ConversationId);
    }

    [Fact]
    public async Task GetOpenByConversationAsync_OrdersDescendingByCreatedAt()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q-A"), CancellationToken.None);
        await fx.QuestionStore.UpdateConversationIdAsync("q-A", "conv-1", CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q-B"), CancellationToken.None);
        await fx.QuestionStore.UpdateConversationIdAsync("q-B", "conv-1", CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q-C"), CancellationToken.None);
        await fx.QuestionStore.UpdateConversationIdAsync("q-C", "conv-OTHER", CancellationToken.None);

        var list = await fx.QuestionStore.GetOpenByConversationAsync("conv-1", CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Equal("q-B", list[0].QuestionId);
        Assert.Equal("q-A", list[1].QuestionId);
    }

    [Fact]
    public async Task GetOpenByConversationAsync_ExcludesResolvedAndExpired()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q-open"), CancellationToken.None);
        await fx.QuestionStore.UpdateConversationIdAsync("q-open", "conv-X", CancellationToken.None);

        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q-done"), CancellationToken.None);
        await fx.QuestionStore.UpdateConversationIdAsync("q-done", "conv-X", CancellationToken.None);
        await fx.QuestionStore.TryUpdateStatusAsync(
            "q-done",
            AgentQuestionStatuses.Open,
            AgentQuestionStatuses.Resolved,
            CancellationToken.None);

        var list = await fx.QuestionStore.GetOpenByConversationAsync("conv-X", CancellationToken.None);
        Assert.Single(list);
        Assert.Equal("q-open", list[0].QuestionId);
    }

    [Fact]
    public async Task GetMostRecentOpenByConversationAsync_ReturnsNewest()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q-A"), CancellationToken.None);
        await fx.QuestionStore.UpdateConversationIdAsync("q-A", "conv-1", CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q-B"), CancellationToken.None);
        await fx.QuestionStore.UpdateConversationIdAsync("q-B", "conv-1", CancellationToken.None);

        var newest = await fx.QuestionStore.GetMostRecentOpenByConversationAsync("conv-1", CancellationToken.None);
        Assert.NotNull(newest);
        Assert.Equal("q-B", newest!.QuestionId);
    }

    [Fact]
    public async Task GetOpenExpiredAsync_RespectsCutoffOrderingAndBatchSize()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        // Three expired and one future.
        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q1", expiresAt: Now.AddMinutes(-30)), CancellationToken.None);
        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q2", expiresAt: Now.AddMinutes(-10)), CancellationToken.None);
        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q3", expiresAt: Now.AddMinutes(-20)), CancellationToken.None);
        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q4", expiresAt: Now.AddMinutes(+30)), CancellationToken.None);

        var batch = await fx.QuestionStore.GetOpenExpiredAsync(Now, batchSize: 2, CancellationToken.None);

        Assert.Equal(2, batch.Count);
        // Earliest expiry first ('q1' is the oldest expired row).
        Assert.Equal("q1", batch[0].QuestionId);
        Assert.Equal("q3", batch[1].QuestionId);
    }

    [Fact]
    public async Task GetOpenExpiredAsync_SkipsAlreadyResolved()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        await fx.QuestionStore.SaveAsync(BuildQuestion(questionId: "q1", expiresAt: Now.AddMinutes(-5)), CancellationToken.None);
        await fx.QuestionStore.TryUpdateStatusAsync(
            "q1",
            AgentQuestionStatuses.Open,
            AgentQuestionStatuses.Resolved,
            CancellationToken.None);

        var batch = await fx.QuestionStore.GetOpenExpiredAsync(Now, 10, CancellationToken.None);
        Assert.Empty(batch);
    }

    [Fact]
    public async Task TryUpdateStatusAsync_RejectsInvalidStatusVocabulary()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);
        await fx.QuestionStore.SaveAsync(BuildQuestion(), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fx.QuestionStore.TryUpdateStatusAsync("q-001", "Open", "NotARealStatus", CancellationToken.None));
    }
}
