using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Sending;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSwarm.Messaging.Tests;

public sealed class TokenBucketTelegramRateLimiterTests
{
    private static IOptions<TelegramOptions> Opts(RateLimitOptions rl) =>
        Options.Create(new TelegramOptions { RateLimits = rl });

    /// <summary>
    /// Acquiring tokens up to the global burst capacity must succeed
    /// immediately (no wait); the next acquisition must observe a
    /// proactive throttle until the global bucket refills. Verifies
    /// the architecture.md §10.4 invariant that the sender does not
    /// rely on the Bot API's 429 response to discover saturation.
    /// </summary>
    [Fact]
    public async Task GlobalBucket_ExhaustedThenRefills()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var rl = new RateLimitOptions
        {
            GlobalPerSecond = 5,
            GlobalBurstCapacity = 5,
            PerChatPerMinute = 1000,
            PerChatBurstCapacity = 1000,
        };
        var sut = new TokenBucketTelegramRateLimiter(Opts(rl), time);

        // Drain the burst: 5 acquires complete synchronously.
        for (var i = 0; i < 5; i++)
        {
            await sut.AcquireAsync(chatId: 42, CancellationToken.None);
        }

        // The 6th request must wait — verify by issuing it on a task
        // and observing it is NOT complete until time advances.
        var pending = sut.AcquireAsync(42, CancellationToken.None);
        pending.IsCompleted.Should().BeFalse(
            "the global bucket is empty so the 6th acquisition must wait for a token to refill");

        // Refill rate is 5/s → one token in 200 ms.
        time.Advance(TimeSpan.FromMilliseconds(250));
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        pending.IsCompleted.Should().BeTrue(
            "after 250 ms at 5 tokens/s the bucket has 1.25 tokens — enough to satisfy the awaiter");
    }

    /// <summary>
    /// Per-chat bucket binds independently of the global bucket so
    /// hot chats throttle without preventing other chats from
    /// sending. Architecture.md §10.4 D-BURST scenario.
    /// </summary>
    [Fact]
    public async Task PerChatBucket_IsIndependentAcrossChats()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var rl = new RateLimitOptions
        {
            GlobalPerSecond = 1000,
            GlobalBurstCapacity = 1000,
            PerChatPerMinute = 60,
            PerChatBurstCapacity = 2,
        };
        var sut = new TokenBucketTelegramRateLimiter(Opts(rl), time);

        // Drain chat 1's burst.
        await sut.AcquireAsync(1, CancellationToken.None);
        await sut.AcquireAsync(1, CancellationToken.None);
        var chat1Pending = sut.AcquireAsync(1, CancellationToken.None);
        chat1Pending.IsCompleted.Should().BeFalse();

        // Chat 2 must still acquire immediately — independent bucket.
        var chat2Acquire = sut.AcquireAsync(2, CancellationToken.None);
        await chat2Acquire.WaitAsync(TimeSpan.FromSeconds(1));
        chat2Acquire.IsCompleted.Should().BeTrue();

        // Refill chat 1 by advancing time (60/min = 1/s).
        time.Advance(TimeSpan.FromSeconds(1.1));
        await chat1Pending.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Iter-1 evaluator item 6 — silently clamping ≤ 0 config values
    /// to 1 hid misconfiguration: operators believed the limiter was
    /// honouring their (wrong) config when it was actually emitting at
    /// the floor. The constructor now throws
    /// <see cref="ArgumentOutOfRangeException"/> for every bucket knob
    /// instead of clamping; the same rule lives in
    /// <see cref="TelegramOptionsValidator"/> so host startup fails
    /// fast, but this guard is the defense-in-depth layer for code
    /// paths (unit tests, ad-hoc DI graphs) that bypass the validator.
    /// </summary>
    [Theory]
    [InlineData(0, 10, 10, 10)]
    [InlineData(-5, 10, 10, 10)]
    [InlineData(10, 0, 10, 10)]
    [InlineData(10, -5, 10, 10)]
    [InlineData(10, 10, 0, 10)]
    [InlineData(10, 10, -5, 10)]
    [InlineData(10, 10, 10, 0)]
    [InlineData(10, 10, 10, -5)]
    public void Constructor_RejectsNonPositiveRateLimitConfig(
        int globalPerSecond,
        int globalBurstCapacity,
        int perChatPerMinute,
        int perChatBurstCapacity)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var rl = new RateLimitOptions
        {
            GlobalPerSecond = globalPerSecond,
            GlobalBurstCapacity = globalBurstCapacity,
            PerChatPerMinute = perChatPerMinute,
            PerChatBurstCapacity = perChatBurstCapacity,
        };

        Action act = () => _ = new TokenBucketTelegramRateLimiter(Opts(rl), time);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "the limiter must fail fast on a misconfigured rate-limit knob — silently clamping would mask the misconfiguration and break the §10.4 SLO envelope");
    }
}
