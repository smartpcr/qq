// -----------------------------------------------------------------------
// <copyright file="SlackTokenBucketRateLimiter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISlackRateLimiter"/> implementation: a
/// per-(tier, scope) token-bucket limiter that hands out tokens at a
/// steady refill rate (derived from <see cref="SlackRateLimitTier.RequestsPerMinute"/>)
/// with an initial burst capacity of <see cref="SlackRateLimitTier.BurstCapacity"/>.
/// On HTTP 429 the bucket for the affected scope is held closed for
/// the duration supplied by the dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// Stage 6.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 5. The limiter is intended to be registered as a SINGLETON
/// so the outbound dispatcher and the modal fast-path's
/// <c>SlackDirectApiClient</c> (Stage 6.4) share the same bucket
/// state and stay collectively within Slack's published tier
/// ceilings.
/// </para>
/// <para>
/// Concurrency model: each <see cref="TokenBucket"/> guards its
/// internal counters with a <see langword="lock"/>; the outer map
/// (<see cref="ConcurrentDictionary{TKey, TValue}"/>) handles
/// concurrent first-touch of unseen (tier, scope) keys without an
/// explicit lock. Threads waiting for a token spin-poll the bucket
/// (<see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>)
/// rather than maintaining a waiter queue -- this trades a small
/// amount of extra wake-ups for a much simpler implementation that
/// remains correct under the dispatcher's expected single-digit
/// concurrency.
/// </para>
/// </remarks>
internal sealed class SlackTokenBucketRateLimiter : ISlackRateLimiter
{
    private readonly IOptionsMonitor<SlackConnectorOptions> optionsMonitor;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<(SlackApiTier Tier, string ScopeKey), TokenBucket> buckets;

    /// <summary>
    /// DI-friendly constructor. Resolves the tier configuration from
    /// <see cref="SlackConnectorOptions.RateLimits"/>; uses
    /// <see cref="TimeProvider.System"/> as the clock so production
    /// hosts get monotonic wall-clock semantics.
    /// </summary>
    public SlackTokenBucketRateLimiter(IOptionsMonitor<SlackConnectorOptions> optionsMonitor)
        : this(optionsMonitor, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets the fixture inject a fake
    /// <see cref="TimeProvider"/> so token-bucket timing is
    /// deterministic.
    /// </summary>
    internal SlackTokenBucketRateLimiter(
        IOptionsMonitor<SlackConnectorOptions> optionsMonitor,
        TimeProvider timeProvider)
    {
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.buckets = new ConcurrentDictionary<(SlackApiTier, string), TokenBucket>();
    }

    /// <inheritdoc />
    public async ValueTask AcquireAsync(SlackApiTier tier, string scopeKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scopeKey))
        {
            throw new ArgumentException("scopeKey must be non-empty.", nameof(scopeKey));
        }

        ct.ThrowIfCancellationRequested();

        TokenBucket bucket = this.ResolveBucket(tier, scopeKey);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan wait = bucket.TryAcquire(this.timeProvider.GetUtcNow().UtcTicks);
            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            // Cap the per-iteration wait so a long Retry-After pause
            // does not hold the loop hostage to cancellation. Task.Delay
            // already observes the cancellation token, but capping also
            // lets the bucket re-evaluate against any newer Suspend()
            // calls (e.g. a second 429 raises the deadline).
            TimeSpan capped = wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait;
            await Task.Delay(capped, this.timeProvider, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void NotifyRetryAfter(SlackApiTier tier, string scopeKey, TimeSpan delay)
    {
        if (string.IsNullOrEmpty(scopeKey))
        {
            throw new ArgumentException("scopeKey must be non-empty.", nameof(scopeKey));
        }

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        TokenBucket bucket = this.ResolveBucket(tier, scopeKey);
        long until = this.timeProvider.GetUtcNow().UtcTicks + delay.Ticks;
        bucket.Suspend(until);
    }

    private TokenBucket ResolveBucket(SlackApiTier tier, string scopeKey)
    {
        return this.buckets.GetOrAdd(
            (tier, scopeKey),
            static (key, state) =>
            {
                SlackTokenBucketRateLimiter self = state;
                SlackRateLimitOptions rateLimits = self.optionsMonitor.CurrentValue.RateLimits ?? new SlackRateLimitOptions();
                SlackRateLimitTier config = SlackOutboundTierMap.ResolveTierConfig(rateLimits, key.Tier);
                double refillTokensPerSecond = Math.Max(0.000001, config.RequestsPerMinute / 60.0);
                double burstCapacity = Math.Max(1.0, config.BurstCapacity);
                return new TokenBucket(
                    refillTokensPerSecond,
                    burstCapacity,
                    self.timeProvider.GetUtcNow().UtcTicks);
            },
            this);
    }

    /// <summary>
    /// Internal helper -- exposed only for white-box assertions in the
    /// Slack test assembly. Returns the number of buckets created so
    /// far; primarily useful for tests that pin "exactly one bucket
    /// per (tier, scope) pair was instantiated".
    /// </summary>
    internal int BucketCount => this.buckets.Count;

    /// <summary>
    /// Single token bucket with a steady refill rate, a burst
    /// capacity, and an optional "suspended until" deadline (set by
    /// <see cref="ISlackRateLimiter.NotifyRetryAfter"/>).
    /// </summary>
    private sealed class TokenBucket
    {
        private readonly object gate = new();
        private readonly double refillTokensPerSecond;
        private readonly double burstCapacity;
        private double availableTokens;
        private long lastRefillUtcTicks;
        private long suspendedUntilUtcTicks;

        public TokenBucket(double refillTokensPerSecond, double burstCapacity, long nowTicks)
        {
            this.refillTokensPerSecond = refillTokensPerSecond;
            this.burstCapacity = burstCapacity;
            this.availableTokens = burstCapacity;
            this.lastRefillUtcTicks = nowTicks;
            this.suspendedUntilUtcTicks = 0;
        }

        /// <summary>
        /// Tries to consume a token. Returns
        /// <see cref="TimeSpan.Zero"/> when a token was consumed; a
        /// positive duration when the caller must wait. The returned
        /// duration is the time UNTIL either a token becomes available
        /// (steady-state refill) OR the pause expires
        /// (suspension), whichever is longer.
        /// </summary>
        public TimeSpan TryAcquire(long nowTicks)
        {
            lock (this.gate)
            {
                if (this.suspendedUntilUtcTicks > nowTicks)
                {
                    return TimeSpan.FromTicks(this.suspendedUntilUtcTicks - nowTicks);
                }

                long elapsedTicks = nowTicks - this.lastRefillUtcTicks;
                if (elapsedTicks > 0)
                {
                    double elapsedSeconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
                    this.availableTokens = Math.Min(
                        this.burstCapacity,
                        this.availableTokens + (elapsedSeconds * this.refillTokensPerSecond));
                    this.lastRefillUtcTicks = nowTicks;
                }

                if (this.availableTokens >= 1.0)
                {
                    this.availableTokens -= 1.0;
                    return TimeSpan.Zero;
                }

                double needed = 1.0 - this.availableTokens;
                double waitSeconds = needed / this.refillTokensPerSecond;
                return TimeSpan.FromSeconds(waitSeconds);
            }
        }

        /// <summary>
        /// Holds the bucket closed at least until
        /// <paramref name="suspendUntilUtcTicks"/>. Monotonic -- a
        /// later, longer pause REPLACES an earlier shorter one; a
        /// shorter pause never shortens an existing longer one.
        /// Tokens are zeroed so a subsequent refill must begin from
        /// the deadline forward.
        /// </summary>
        public void Suspend(long suspendUntilUtcTicks)
        {
            lock (this.gate)
            {
                if (suspendUntilUtcTicks > this.suspendedUntilUtcTicks)
                {
                    this.suspendedUntilUtcTicks = suspendUntilUtcTicks;
                    this.availableTokens = 0;
                    this.lastRefillUtcTicks = suspendUntilUtcTicks;
                }
            }
        }
    }
}
