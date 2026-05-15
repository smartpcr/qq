using Xunit;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

public sealed class SqlCardStateStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static TeamsCardState BuildState(
        string questionId = "q-001",
        string activityId = "act-001",
        string conversationId = "conv-001",
        string status = TeamsCardStatuses.Pending,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        return new TeamsCardState
        {
            QuestionId = questionId,
            ActivityId = activityId,
            ConversationId = conversationId,
            ConversationReferenceJson = "{\"channelId\":\"msteams\"}",
            Status = status,
            CreatedAt = createdAt ?? Now,
            UpdatedAt = updatedAt ?? Now,
        };
    }

    [Fact]
    public async Task SaveAsync_ThenGet_RoundTripsAllFields()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        await fx.CardStateStore.SaveAsync(BuildState(), CancellationToken.None);

        var loaded = await fx.CardStateStore.GetByQuestionIdAsync("q-001", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("act-001", loaded!.ActivityId);
        Assert.Equal("conv-001", loaded.ConversationId);
        Assert.Equal("{\"channelId\":\"msteams\"}", loaded.ConversationReferenceJson);
        Assert.Equal(TeamsCardStatuses.Pending, loaded.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_AnsweredOrExpired_Persists()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        await fx.CardStateStore.SaveAsync(BuildState(), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await fx.CardStateStore.UpdateStatusAsync("q-001", TeamsCardStatuses.Answered, CancellationToken.None);

        var loaded = await fx.CardStateStore.GetByQuestionIdAsync("q-001", CancellationToken.None);
        Assert.Equal(TeamsCardStatuses.Answered, loaded!.Status);
        Assert.Equal(Now.AddMinutes(1), loaded.UpdatedAt);
    }

    [Fact]
    public async Task UpdateStatusAsync_ExpiredIsValid_ForDeletePath()
    {
        // This pins the iter-5 critique #2 fix: after DeleteCardAsync the card-state
        // lands at TeamsCardStatuses.Expired (not a new "Deleted" status). The canonical
        // vocabulary is Pending/Answered/Expired only.
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        await fx.CardStateStore.SaveAsync(BuildState(), CancellationToken.None);
        await fx.CardStateStore.UpdateStatusAsync("q-001", TeamsCardStatuses.Expired, CancellationToken.None);

        var loaded = await fx.CardStateStore.GetByQuestionIdAsync("q-001", CancellationToken.None);
        Assert.Equal(TeamsCardStatuses.Expired, loaded!.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_RejectsInvalidStatus()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);
        await fx.CardStateStore.SaveAsync(BuildState(), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fx.CardStateStore.UpdateStatusAsync("q-001", "Deleted", CancellationToken.None));
    }

    [Fact]
    public async Task GetByQuestionIdAsync_ReturnsNull_WhenMissing()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);

        var loaded = await fx.CardStateStore.GetByQuestionIdAsync("does-not-exist", CancellationToken.None);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task SaveAsync_UpsertReplaces_ExistingRow()
    {
        var clock = new FakeTimeProvider(Now);
        await using var fx = new LifecycleStoreFixture(clock);
        await fx.CardStateStore.SaveAsync(BuildState(activityId: "act-A"), CancellationToken.None);

        // Proactive resend captures a new ActivityId / ConversationReference — Save
        // should overwrite the prior row rather than collide on the QuestionId PK.
        await fx.CardStateStore.SaveAsync(BuildState(activityId: "act-B"), CancellationToken.None);

        var loaded = await fx.CardStateStore.GetByQuestionIdAsync("q-001", CancellationToken.None);
        Assert.Equal("act-B", loaded!.ActivityId);
    }

    [Fact]
    public async Task SaveAsync_RejectsBlankRequiredFields()
    {
        await using var fx = new LifecycleStoreFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fx.CardStateStore.SaveAsync(
                new TeamsCardState
                {
                    QuestionId = string.Empty,
                    ActivityId = "act",
                    ConversationId = "conv",
                    ConversationReferenceJson = "{}",
                    CreatedAt = Now,
                    UpdatedAt = Now,
                },
                CancellationToken.None));
    }
}
