// -----------------------------------------------------------------------
// <copyright file="SlackTokenBucketRateLimiterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 6.3 unit tests for <see cref="SlackTokenBucketRateLimiter"/>.
/// Covers burst capacity, refill timing, scope isolation, and
/// <c>Retry-After</c> suspension.
/// </summary>
public sealed class SlackTokenBucketRateLimiterTests
{
    private const string TeamA = "T-A";
    private const string ChannelA = "C-A";
    private const string ChannelB = "C-B";

    [Fact]
    public async Task AcquireAsync_returns_immediately_within_burst_capacity()
    {
        // Tier 2 default: BurstCapacity = 5. The first five
        // AcquireAsync calls on the same scope MUST return without
        // any noticeable wait.
        SlackTokenBucketRateLimiter limiter = BuildLimiter();

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);
        }

        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "the burst capacity (5 tokens) must absorb the first 5 calls without throttling");
    }

    [Fact]
    public async Task AcquireAsync_throttles_after_burst_exhausted()
    {
        // Tier 2 default: 20 req/min steady-state -> 1 token per 3
        // seconds. Drain the bucket, then a sixth acquire must wait
        // at least until the bucket refills enough to release another
        // token. We use a smaller test config (high RPM) so the test
        // does not actually wait 3 seconds.
        SlackTokenBucketRateLimiter limiter = BuildLimiter(new SlackRateLimitOptions
        {
            Tier2 = new SlackRateLimitTier
            {
                RequestsPerMinute = 600, // 10 tokens / sec
                BurstCapacity = 2,
                Scope = SlackRateLimitScope.Channel,
            },
        });

        // Drain the two-token bucket.
        await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);
        await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);

        Stopwatch sw = Stopwatch.StartNew();
        await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(50),
            "after the burst is drained, the third acquire must wait for the bucket to refill");
    }

    [Fact]
    public async Task AcquireAsync_creates_independent_buckets_per_scope()
    {
        // Channel-scoped tier (Tier 2): different scope keys must
        // hold independent token buckets so a 429 on one channel
        // does not throttle a sibling channel.
        SlackTokenBucketRateLimiter limiter = BuildLimiter();

        for (int i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);
        }

        // ChannelB should still have a full burst because it lives
        // in a different bucket. This call should be near-instant.
        Stopwatch sw = Stopwatch.StartNew();
        await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelB}", CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200),
            "a per-channel tier must isolate buckets between channels");
        limiter.BucketCount.Should().Be(2,
            "two distinct scope keys must materialise two buckets");
    }

    [Fact]
    public async Task NotifyRetryAfter_suspends_bucket_for_supplied_duration()
    {
        SlackTokenBucketRateLimiter limiter = BuildLimiter(new SlackRateLimitOptions
        {
            Tier2 = new SlackRateLimitTier
            {
                RequestsPerMinute = 6000, // 100 tokens / sec -- effectively no steady-state throttle
                BurstCapacity = 100,
                Scope = SlackRateLimitScope.Channel,
            },
        });

        await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);

        // Suspend the bucket for ~250 ms; the next acquire must wait
        // out the suspension before returning.
        limiter.NotifyRetryAfter(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", TimeSpan.FromMilliseconds(250));

        Stopwatch sw = Stopwatch.StartNew();
        await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(150),
            "the Retry-After window must hold the bucket closed for at least the requested duration");
    }

    [Fact]
    public async Task NotifyRetryAfter_is_monotonic_does_not_shorten_existing_pause()
    {
        SlackTokenBucketRateLimiter limiter = BuildLimiter(new SlackRateLimitOptions
        {
            Tier2 = new SlackRateLimitTier
            {
                RequestsPerMinute = 6000,
                BurstCapacity = 100,
                Scope = SlackRateLimitScope.Channel,
            },
        });

        await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);

        // First pause is long, second pause is short -- the short one
        // MUST NOT shorten the existing longer pause.
        limiter.NotifyRetryAfter(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", TimeSpan.FromMilliseconds(400));
        limiter.NotifyRetryAfter(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", TimeSpan.FromMilliseconds(50));

        Stopwatch sw = Stopwatch.StartNew();
        await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(300),
            "a later shorter Retry-After pause MUST NOT cancel an earlier longer one");
    }

    [Fact]
    public async Task AcquireAsync_propagates_cancellation()
    {
        SlackTokenBucketRateLimiter limiter = BuildLimiter();

        // Drain the burst then issue a suspension that would exceed
        // the test budget, then cancel the acquire and verify it
        // propagates OperationCanceledException promptly.
        for (int i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", CancellationToken.None);
        }

        limiter.NotifyRetryAfter(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", TimeSpan.FromMinutes(5));

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        Func<Task> act = async () => await limiter.AcquireAsync(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void NotifyRetryAfter_ignores_non_positive_delay()
    {
        SlackTokenBucketRateLimiter limiter = BuildLimiter();

        Action act = () => limiter.NotifyRetryAfter(SlackApiTier.Tier2, $"{TeamA}:{ChannelA}", TimeSpan.Zero);

        act.Should().NotThrow();
        limiter.BucketCount.Should().Be(0,
            "a zero-delay suspension must NOT materialise a bucket so the limiter stays empty for unused scopes");
    }

    [Fact]
    public void AcquireAsync_throws_on_empty_scope_key()
    {
        SlackTokenBucketRateLimiter limiter = BuildLimiter();

        Func<Task> act = async () =>
            await limiter.AcquireAsync(SlackApiTier.Tier2, string.Empty, CancellationToken.None);

        act.Should().ThrowAsync<ArgumentException>();
    }

    private static SlackTokenBucketRateLimiter BuildLimiter(SlackRateLimitOptions? rateLimits = null)
    {
        SlackConnectorOptions opts = new()
        {
            RateLimits = rateLimits ?? new SlackRateLimitOptions(),
        };

        return new SlackTokenBucketRateLimiter(
            new StaticOptionsMonitor<SlackConnectorOptions>(opts),
            TimeProvider.System);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T value;

        public StaticOptionsMonitor(T value)
        {
            this.value = value;
        }

        public T CurrentValue => this.value;

        public T Get(string? name) => this.value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
