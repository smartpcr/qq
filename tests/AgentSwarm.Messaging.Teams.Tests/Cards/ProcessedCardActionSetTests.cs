using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Schema;

namespace AgentSwarm.Messaging.Teams.Tests.Cards;

/// <summary>
/// Unit tests for the Stage 6.2 in-memory processed-action set
/// (<see cref="ProcessedCardActionSet"/>) — covers the tuple-key dedupe, prior-response
/// replay, eviction, and explicit removal contracts described in
/// <c>implementation-plan.md</c> §6.2 and <c>architecture.md</c> §2.6 layer 2.
/// </summary>
public sealed class ProcessedCardActionSetTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static AdaptiveCardInvokeResponse BuildResponse(string body) => new()
    {
        StatusCode = 200,
        Type = "application/vnd.microsoft.activity.message",
        Value = body,
    };

    [Fact]
    public void TryClaim_FirstCallSucceeds_SecondCallReturnsPreviousResponse()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var set = new ProcessedCardActionSet(new CardActionDedupeOptions(), clock);
        var key = (QuestionId: "q-1", UserId: "user-1");

        var claimed = set.TryClaim(key, out var firstPrev);
        Assert.True(claimed);
        Assert.Null(firstPrev);

        // Cache the terminal response, then replay.
        var response = BuildResponse("Recorded approve for q-1.");
        set.RecordResult(key, response);

        var second = set.TryClaim(key, out var prev);
        Assert.False(second);
        Assert.NotNull(prev);
        Assert.Same(response, prev);
    }

    [Fact]
    public void TryClaim_DifferentUserSameQuestion_NotDeduped()
    {
        var set = new ProcessedCardActionSet();
        Assert.True(set.TryClaim(("q-1", "user-a"), out _));
        Assert.True(set.TryClaim(("q-1", "user-b"), out _));
    }

    [Fact]
    public void TryClaim_SameUserDifferentQuestion_NotDeduped()
    {
        var set = new ProcessedCardActionSet();
        Assert.True(set.TryClaim(("q-1", "user-a"), out _));
        Assert.True(set.TryClaim(("q-2", "user-a"), out _));
    }

    [Fact]
    public void Remove_ReleasesSlot_PermitsRetry()
    {
        var set = new ProcessedCardActionSet();
        Assert.True(set.TryClaim(("q-1", "user-1"), out _));
        Assert.False(set.TryClaim(("q-1", "user-1"), out _));

        set.Remove(("q-1", "user-1"));

        Assert.True(set.TryClaim(("q-1", "user-1"), out _));
    }

    [Fact]
    public void EvictExpired_RemovesEntriesOlderThanLifetime()
    {
        // Stage 6.2 test scenario: expired processed-action entries cleaned.
        var start = DateTimeOffset.UnixEpoch;
        var clock = new FixedTimeProvider(start);
        var set = new ProcessedCardActionSet(
            new CardActionDedupeOptions { EntryLifetime = TimeSpan.FromHours(24) },
            clock);

        Assert.True(set.TryClaim(("q-old", "user-1"), out _));

        // Advance clock past lifetime — entry should now be evictable.
        clock.Advance(TimeSpan.FromHours(25));
        Assert.True(set.TryClaim(("q-new", "user-2"), out _));

        Assert.Equal(2, set.Count);
        var removed = set.EvictExpired();
        Assert.Equal(1, removed);
        Assert.Equal(1, set.Count);

        // The fresh entry is still present.
        Assert.False(set.TryClaim(("q-new", "user-2"), out _));
        // The expired entry was released.
        Assert.True(set.TryClaim(("q-old", "user-1"), out _));
    }

    [Fact]
    public void EvictExpired_OnEmptySet_ReturnsZero()
    {
        var set = new ProcessedCardActionSet();
        Assert.Equal(0, set.EvictExpired());
    }

    [Fact]
    public void TryClaim_NullKeyComponents_Throws()
    {
        var set = new ProcessedCardActionSet();
        Assert.Throws<ArgumentException>(() => set.TryClaim((null!, "u"), out _));
        Assert.Throws<ArgumentException>(() => set.TryClaim(("q", null!), out _));
        Assert.Throws<ArgumentException>(() => set.TryClaim((string.Empty, "u"), out _));
        Assert.Throws<ArgumentException>(() => set.TryClaim(("q", string.Empty), out _));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveLifetime()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProcessedCardActionSet(
                new CardActionDedupeOptions { EntryLifetime = TimeSpan.Zero },
                TimeProvider.System));
    }

    [Fact]
    public void RecordResult_OverwritesPriorResponse()
    {
        var set = new ProcessedCardActionSet();
        var key = (QuestionId: "q-1", UserId: "user-1");
        set.TryClaim(key, out _);

        var first = BuildResponse("first");
        var second = BuildResponse("second");
        set.RecordResult(key, first);
        set.RecordResult(key, second);

        set.TryClaim(key, out var observed);
        Assert.Same(second, observed);
    }

    [Fact]
    public void Defaults_Are24HoursAnd5MinuteEviction()
    {
        // Pins the canonical Stage 6.2 brief defaults so a future refactor cannot
        // silently change the TTL or eviction cadence.
        var opts = new CardActionDedupeOptions();
        Assert.Equal(TimeSpan.FromHours(24), opts.EntryLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), opts.EvictionInterval);
    }

    [Fact]
    public async Task Claim_FirstCallOwnsSlot_SecondCallAwaitsInFlightCompletion()
    {
        // Iter-2 evaluator fix #2 — the canonical Claim API returns a Task that
        // resolves when the in-flight first caller records its terminal response,
        // even if the second caller arrives BEFORE the first finishes.
        var set = new ProcessedCardActionSet();
        var key = (QuestionId: "q-1", UserId: "user-1");

        var first = set.Claim(key);
        Assert.True(first.IsOwner);
        Assert.True(first.PreviousResponseTask.IsCompletedSuccessfully);
        Assert.Null(await first.PreviousResponseTask);

        // Second concurrent claim — observes the in-flight entry. Task is NOT yet
        // completed because RecordResult has not been called.
        var second = set.Claim(key);
        Assert.False(second.IsOwner);
        Assert.False(second.PreviousResponseTask.IsCompleted);

        // First call records its terminal response — second call's task resolves.
        var response = BuildResponse("approve recorded");
        set.RecordResult(key, response);

        var replayed = await second.PreviousResponseTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(response, replayed);
    }

    [Fact]
    public async Task Claim_RemoveSignalsWaitersWithNull_PermitsRetry()
    {
        // When the first caller fails and calls Remove, in-flight waiters must see
        // null so they re-claim and run the pipeline themselves.
        var set = new ProcessedCardActionSet();
        var key = (QuestionId: "q-1", UserId: "user-1");

        var first = set.Claim(key);
        Assert.True(first.IsOwner);

        var waiter = set.Claim(key);
        Assert.False(waiter.IsOwner);

        set.Remove(key);

        var observed = await waiter.PreviousResponseTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(observed);

        // The slot is released — a fresh claim succeeds.
        var fresh = set.Claim(key);
        Assert.True(fresh.IsOwner);
    }

    [Fact]
    public async Task EvictExpired_SignalsInFlightWaitersWithNull()
    {
        // If the eviction service runs while a claim is still in flight, the entry is
        // removed and the in-flight waiter is signalled with null so it can retry.
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var set = new ProcessedCardActionSet(
            new CardActionDedupeOptions { EntryLifetime = TimeSpan.FromMinutes(1) },
            clock);
        var key = (QuestionId: "q-1", UserId: "user-1");

        var owner = set.Claim(key);
        Assert.True(owner.IsOwner);

        var waiter = set.Claim(key);
        Assert.False(waiter.IsOwner);

        // Advance past lifetime, then evict.
        clock.Advance(TimeSpan.FromMinutes(2));
        set.EvictExpired();

        var observed = await waiter.PreviousResponseTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(observed);
    }
}
