// -----------------------------------------------------------------------
// <copyright file="SlackInProcessIdempotencyStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 4.1 unit tests for
/// <see cref="SlackInProcessIdempotencyStore"/>. Pins the
/// acquire / duplicate / release / expiry contract that the
/// <see cref="DefaultSlackModalFastPathHandler"/> relies on.
/// </summary>
public sealed class SlackInProcessIdempotencyStoreTests
{
    [Fact]
    public async Task First_acquire_returns_true()
    {
        SlackInProcessIdempotencyStore store = new();
        bool acquired = await store.TryAcquireAsync("cmd:T:U:/agent:trig.1", TimeSpan.FromMinutes(1), CancellationToken.None);
        acquired.Should().BeTrue("a previously-unseen key must be acquired");
        store.LiveCount.Should().Be(1);
    }

    [Fact]
    public async Task Second_acquire_within_ttl_returns_false()
    {
        SlackInProcessIdempotencyStore store = new();
        await store.TryAcquireAsync("dup", TimeSpan.FromMinutes(1), CancellationToken.None);

        bool dup = await store.TryAcquireAsync("dup", TimeSpan.FromMinutes(1), CancellationToken.None);
        dup.Should().BeFalse(
            "a key acquired within its TTL window must be reported as a duplicate so the fast-path can silently ACK");
    }

    [Fact]
    public async Task Acquire_after_ttl_expires_returns_true_again()
    {
        ManualClock clock = new(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        SlackInProcessIdempotencyStore store = new(clock);

        (await store.TryAcquireAsync("k", TimeSpan.FromSeconds(10), CancellationToken.None))
            .Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(11));

        (await store.TryAcquireAsync("k", TimeSpan.FromSeconds(10), CancellationToken.None))
            .Should().BeTrue(
                "once the TTL has expired the key becomes available again so a retry that arrived after the window can succeed");
    }

    [Fact]
    public async Task Release_lets_the_same_key_be_reacquired()
    {
        SlackInProcessIdempotencyStore store = new();
        (await store.TryAcquireAsync("k", TimeSpan.FromMinutes(15), CancellationToken.None))
            .Should().BeTrue();

        store.Release("k");

        (await store.TryAcquireAsync("k", TimeSpan.FromMinutes(15), CancellationToken.None))
            .Should().BeTrue(
                "Release must clear the entry so a failing fast-path run does not lock the user out of retrying");
    }

    [Fact]
    public async Task Empty_key_throws()
    {
        SlackInProcessIdempotencyStore store = new();
        Func<Task> act = async () => await store.TryAcquireAsync(string.Empty, TimeSpan.FromSeconds(1), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>(
            "the store requires a non-empty key so a missing idempotency anchor cannot collapse to a shared sentinel");
    }

    /// <summary>
    /// Tiny <see cref="TimeProvider"/> stub for the TTL-expiry test.
    /// Lives next to the test that needs it so the test project does
    /// not have to take a dependency on Microsoft.Extensions.TimeProvider.Testing.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now;

        public ManualClock(DateTimeOffset start)
        {
            this.now = start;
        }

        public void Advance(TimeSpan delta) => this.now = this.now.Add(delta);

        public override DateTimeOffset GetUtcNow() => this.now;
    }
}
