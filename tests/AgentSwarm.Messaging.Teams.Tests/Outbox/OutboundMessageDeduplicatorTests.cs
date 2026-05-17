using AgentSwarm.Messaging.Teams.Outbox;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// Unit tests for the Stage 6.2 step 4 outbound deduplicator
/// (<see cref="OutboundMessageDeduplicator"/>) — covers the canonical "same
/// CorrelationId + DestinationId within the window is suppressed" behaviour, the
/// per-correlation isolation guarantee, the lazy stale-entry release, and the explicit
/// background eviction.
/// </summary>
public sealed class OutboundMessageDeduplicatorTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    [Fact]
    public void TryRegister_FirstCallTrue_SecondCallFalse_WithinWindow()
    {
        // Stage 6.2 test scenario: "Outbound deduplication — Given a message with
        // CorrelationId = c-1 and DestinationId = d-1 was already sent within the
        // dedupe window, When SendMessageAsync is called with the same CorrelationId
        // + DestinationId, Then the duplicate send is suppressed."
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var dedup = new OutboundMessageDeduplicator(
            new OutboundDeduplicationOptions { Window = TimeSpan.FromMinutes(10) },
            clock);

        Assert.True(dedup.TryRegister("c-1", "d-1"));
        Assert.False(dedup.TryRegister("c-1", "d-1"));
    }

    [Fact]
    public void TryRegister_DifferentCorrelationId_NotDeduped()
    {
        var dedup = new OutboundMessageDeduplicator();
        Assert.True(dedup.TryRegister("c-1", "d-1"));
        Assert.True(dedup.TryRegister("c-2", "d-1"));
    }

    [Fact]
    public void TryRegister_DifferentDestination_NotDeduped()
    {
        var dedup = new OutboundMessageDeduplicator();
        Assert.True(dedup.TryRegister("c-1", "d-1"));
        Assert.True(dedup.TryRegister("c-1", "d-2"));
    }

    [Fact]
    public void TryRegister_AfterWindowElapses_RegistersAsFresh()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var dedup = new OutboundMessageDeduplicator(
            new OutboundDeduplicationOptions { Window = TimeSpan.FromMinutes(1) },
            clock);

        Assert.True(dedup.TryRegister("c-1", "d-1"));
        clock.Advance(TimeSpan.FromMinutes(2));
        // After the window elapses, the same (CorrelationId, DestinationId) tuple is
        // treated as a fresh delivery (lazy in-line eviction kicks in).
        Assert.True(dedup.TryRegister("c-1", "d-1"));
    }

    [Fact]
    public void EvictExpired_RemovesAgedEntries()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var dedup = new OutboundMessageDeduplicator(
            new OutboundDeduplicationOptions { Window = TimeSpan.FromMinutes(1) },
            clock);

        Assert.True(dedup.TryRegister("c-stale", "d-1"));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(dedup.TryRegister("c-fresh", "d-1"));

        Assert.Equal(2, dedup.Count);
        var removed = dedup.EvictExpired();
        Assert.Equal(1, removed);
        Assert.Equal(1, dedup.Count);
    }

    [Fact]
    public void TryRegister_NullOrEmptyArgs_Throws()
    {
        var dedup = new OutboundMessageDeduplicator();
        Assert.Throws<ArgumentException>(() => dedup.TryRegister(null!, "d"));
        Assert.Throws<ArgumentException>(() => dedup.TryRegister("c", null!));
        Assert.Throws<ArgumentException>(() => dedup.TryRegister(string.Empty, "d"));
        Assert.Throws<ArgumentException>(() => dedup.TryRegister("c", string.Empty));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveWindow()
    {
        Assert.Throws<ArgumentException>(() =>
            new OutboundMessageDeduplicator(
                new OutboundDeduplicationOptions { Window = TimeSpan.Zero },
                TimeProvider.System));
    }

    [Fact]
    public void Remove_ReleasesSlot_PermitsRetry()
    {
        // Iter-2 evaluator fix #1 — Remove releases a previously-registered slot so
        // OutboxBackedMessengerConnector can roll back the registration when the
        // downstream enqueue throws (without rollback the dedupe window would silently
        // poison every retry).
        var dedup = new OutboundMessageDeduplicator();
        Assert.True(dedup.TryRegister("c-1", "d-1"));
        Assert.False(dedup.TryRegister("c-1", "d-1"));

        Assert.True(dedup.Remove("c-1", "d-1"));

        Assert.True(dedup.TryRegister("c-1", "d-1"));
        Assert.Equal(1, dedup.Count);
    }

    [Fact]
    public void Remove_AbsentEntry_ReturnsFalse_NoThrow()
    {
        var dedup = new OutboundMessageDeduplicator();
        Assert.False(dedup.Remove("nonexistent", "key"));
    }

    [Fact]
    public void Remove_OnlyAffectsTargetedKey()
    {
        var dedup = new OutboundMessageDeduplicator();
        Assert.True(dedup.TryRegister("c-1", "d-1"));
        Assert.True(dedup.TryRegister("c-2", "d-1"));

        Assert.True(dedup.Remove("c-1", "d-1"));

        // Only the c-1 entry was removed — c-2 is still registered.
        Assert.False(dedup.TryRegister("c-2", "d-1"));
        Assert.True(dedup.TryRegister("c-1", "d-1"));
    }

    [Fact]
    public async Task Claim_FirstCallOwnsSlot_SecondCallAwaitsCommit_ResolvesTrueOnCommit()
    {
        // Iter-3 evaluator fix #1 — the canonical Claim API: the loser must AWAIT the
        // owner's terminal outcome rather than returning success-shaped immediately.
        // When the owner commits, the loser sees `true` and suppresses as a real
        // duplicate.
        var dedup = new OutboundMessageDeduplicator();

        var ownerClaim = dedup.Claim("c-1", "d-1");
        Assert.True(ownerClaim.IsOwner);

        var loserClaim = dedup.Claim("c-1", "d-1");
        Assert.False(loserClaim.IsOwner);
        Assert.False(loserClaim.WinnerOutcomeTask.IsCompleted);

        dedup.Commit("c-1", "d-1");

        var winnerOutcome = await loserClaim.WinnerOutcomeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(winnerOutcome);
    }

    [Fact]
    public async Task Claim_FirstCallOwnsSlot_SecondCallAwaitsRemove_ResolvesFalseOnRemove()
    {
        // Iter-3 evaluator fix #1 — when the owner rolls back (transient failure),
        // the loser's WinnerOutcomeTask resolves to `false` so the loser can re-claim
        // and run the pipeline themselves rather than silently no-op.
        var dedup = new OutboundMessageDeduplicator();

        var ownerClaim = dedup.Claim("c-1", "d-1");
        Assert.True(ownerClaim.IsOwner);

        var loserClaim = dedup.Claim("c-1", "d-1");
        Assert.False(loserClaim.IsOwner);
        Assert.False(loserClaim.WinnerOutcomeTask.IsCompleted);

        Assert.True(dedup.Remove("c-1", "d-1"));

        var winnerOutcome = await loserClaim.WinnerOutcomeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(winnerOutcome);

        // The loser should now be able to re-claim as the new owner.
        var secondOwner = dedup.Claim("c-1", "d-1");
        Assert.True(secondOwner.IsOwner);
    }

    [Fact]
    public async Task EvictExpired_SignalsInFlightWaitersWithFalse_PermitsRetry()
    {
        // Pathological edge case — if a winner hangs past the window without calling
        // Commit or Remove, the eviction service must signal waiters so they retry
        // rather than treating the hung pipeline as a successful commit.
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var dedup = new OutboundMessageDeduplicator(
            new OutboundDeduplicationOptions { Window = TimeSpan.FromMinutes(1) },
            clock);

        var ownerClaim = dedup.Claim("c-1", "d-1");
        Assert.True(ownerClaim.IsOwner);

        var loserClaim = dedup.Claim("c-1", "d-1");
        Assert.False(loserClaim.IsOwner);

        // Advance past the window without committing — eviction must signal waiters.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, dedup.EvictExpired());

        var winnerOutcome = await loserClaim.WinnerOutcomeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(winnerOutcome);
    }

    [Fact]
    public async Task Claim_OwnerCommit_ThenLaterClaim_SuppressesAsCommittedDuplicate()
    {
        // After Commit, subsequent claims within the window observe IsOwner=false and
        // an already-completed WinnerOutcomeTask resolving to `true` — they should
        // suppress without waiting.
        var dedup = new OutboundMessageDeduplicator();

        var ownerClaim = dedup.Claim("c-1", "d-1");
        Assert.True(ownerClaim.IsOwner);
        dedup.Commit("c-1", "d-1");

        var laterClaim = dedup.Claim("c-1", "d-1");
        Assert.False(laterClaim.IsOwner);
        Assert.True(laterClaim.WinnerOutcomeTask.IsCompleted);
        var outcome = await laterClaim.WinnerOutcomeTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(outcome);
    }

    [Fact]
    public void Commit_NoExistingEntry_NoThrow()
    {
        // Commit is idempotent / forgiving — it must not throw if the entry was
        // already evicted between the owner's claim and the commit (e.g. a long-paused
        // pipeline that crossed a window boundary).
        var dedup = new OutboundMessageDeduplicator();
        dedup.Commit("missing", "key");
    }
}
