// -----------------------------------------------------------------------
// <copyright file="RetryPolicyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using AgentSwarm.Messaging.Core;
using FluentAssertions;

/// <summary>
/// Stage 4.2 — pins <see cref="RetryPolicy.ComputeDelay"/> against
/// the brief's documented contract: defaults aligned with
/// architecture.md §5.3, exponential growth bounded by
/// <see cref="RetryPolicy.MaxDelayMs"/>, jitter band of ±<see cref="RetryPolicy.JitterPercent"/>
/// percent, and ≤0 attempt clamping.
/// </summary>
public sealed class RetryPolicyTests
{
    /// <summary>
    /// Seeded <see cref="Random"/> for deterministic jitter draws.
    /// The seed value is irrelevant — what matters is that every
    /// assertion that references "the random draw" reads from the
    /// same instance so a regression in the math (not in the jitter
    /// source) surfaces a stable failure.
    /// </summary>
    private static Random NewSeededRandom() => new(42);

    [Fact]
    public void Defaults_MatchArchitectureMd_Section_5_3()
    {
        // Brief: "MaxAttempts (default 5, aligned with architecture.md
        // §5.3 OutboundQueue:MaxRetries default of 5 ...), InitialDelayMs
        // (default 2000, aligned with architecture.md BaseRetryDelaySeconds
        // default of 2), BackoffMultiplier (default 2.0), MaxDelayMs
        // (default 30000), JitterPercent (default 25, aligned with
        // architecture.md ±25% jitter)".
        var policy = new RetryPolicy();

        policy.MaxAttempts.Should().Be(5,
            "architecture.md §5.3 OutboundQueue:MaxRetries default of 5 — the two knobs cannot drift");
        policy.InitialDelayMs.Should().Be(2000,
            "architecture.md §5.3 BaseRetryDelaySeconds default of 2 seconds");
        policy.BackoffMultiplier.Should().Be(2.0,
            "architecture.md §5.3 exponential curve 2s, 4s, 8s, ...");
        policy.MaxDelayMs.Should().Be(30000,
            "architecture.md §5.3 caps the exponential curve so the dead-letter verdict happens within a bounded wall-clock window");
        policy.JitterPercent.Should().Be(25,
            "architecture.md §5.3 ±25% jitter band to prevent thundering herd");
    }

    [Fact]
    public void ComputeDelay_FirstAttempt_ApproximatesInitialDelayMs_WithinJitterBand()
    {
        // Scenario: Retry with backoff — Given a message fails on
        // first attempt, When retried, Then the delay before the
        // second attempt is approximately InitialDelayMs (within
        // jitter tolerance). With JitterPercent=25 the delay falls
        // in [InitialDelayMs * 0.75, InitialDelayMs * 1.25].
        var policy = new RetryPolicy();
        var rng = NewSeededRandom();

        for (var i = 0; i < 100; i++)
        {
            var delay = policy.ComputeDelay(attempt: 1, rng);

            delay.TotalMilliseconds.Should().BeInRange(
                policy.InitialDelayMs * 0.75,
                policy.InitialDelayMs * 1.25,
                "the first-attempt delay must be base ± 25%: any draw outside [1500, 2500] ms means the jitter math regressed");
        }
    }

    [Fact]
    public void ComputeDelay_ExponentialGrowth_PerAttempt_Until_MaxDelayMs_Cap()
    {
        // With InitialDelayMs=2000 and BackoffMultiplier=2.0:
        //   attempt=1 → base 2000  → [1500,  2500]
        //   attempt=2 → base 4000  → [3000,  5000]
        //   attempt=3 → base 8000  → [6000, 10000]
        //   attempt=4 → base 16000 → [12000, 20000]
        //   attempt=5 → base 32000 → capped to MaxDelayMs=30000 → [22500, 37500]
        var policy = new RetryPolicy();
        var rng = NewSeededRandom();

        (int attempt, double minMs, double maxMs)[] expected =
        {
            (1, 1500, 2500),
            (2, 3000, 5000),
            (3, 6000, 10000),
            (4, 12000, 20000),
        };

        foreach (var (attempt, minMs, maxMs) in expected)
        {
            var delay = policy.ComputeDelay(attempt, rng);
            delay.TotalMilliseconds.Should().BeInRange(
                minMs,
                maxMs,
                $"attempt {attempt} should land in the ±25% band of base = InitialDelayMs * BackoffMultiplier^(attempt-1)");
        }

        // attempt=5 — base would be 32000ms but the MaxDelayMs cap
        // brings it back to 30000ms. The jitter band is ±25% of the
        // capped value (30000), so [22500, 37500].
        var cappedDelay = policy.ComputeDelay(attempt: 5, rng);
        cappedDelay.TotalMilliseconds.Should().BeLessThanOrEqualTo(
            policy.MaxDelayMs * 1.25,
            "attempt 5's exponential base would be 32000ms but the MaxDelayMs cap kicks in before jitter is applied — the upper bound is therefore MaxDelayMs + 25%");
        cappedDelay.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(
            policy.MaxDelayMs * 0.75,
            "attempt 5's lower-bound jitter draw is still MaxDelayMs - 25%");
    }

    [Fact]
    public void ComputeDelay_LargeAttempt_ClampedTo_MaxDelayMs_Band()
    {
        // Edge case: a runaway attempt number must NOT cause integer
        // overflow or an unbounded delay — the MaxDelayMs cap is the
        // hard ceiling for the base value, jitter then floats the
        // result inside ±25%.
        var policy = new RetryPolicy();
        var rng = NewSeededRandom();

        for (var attempt = 6; attempt <= 50; attempt++)
        {
            var delay = policy.ComputeDelay(attempt, rng);
            delay.TotalMilliseconds.Should().BeLessThanOrEqualTo(
                policy.MaxDelayMs * 1.25,
                $"attempt {attempt}: the cap must apply BEFORE jitter so a degenerate exponential cannot stall the row for >MaxDelayMs * 1.25 ms");
            delay.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(
                0,
                "the delay must never be negative — jitter is ± so the lower bound clamps to 0");
        }
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ComputeDelay_NonPositiveAttempt_BehavesAsAttempt1(int attempt)
    {
        // The brief says ComputeDelay(attempt) uses 1-based numbering.
        // A negative or zero attempt is a caller bug — the contract
        // is to clamp to attempt=1 (the first-retry delay) rather
        // than throw, so the worker's retry path never crashes mid-
        // failure-handling because of an off-by-one upstream.
        var policy = new RetryPolicy();
        var rng = NewSeededRandom();

        var delay = policy.ComputeDelay(attempt, rng);

        delay.TotalMilliseconds.Should().BeInRange(
            policy.InitialDelayMs * 0.75,
            policy.InitialDelayMs * 1.25,
            $"attempt={attempt} must be clamped to the first-retry delay band, NOT throw or produce a negative result");
    }

    [Fact]
    public void ComputeDelay_ZeroJitter_IsExactlyBaseDelay()
    {
        // The "deterministic mode" contract: setting JitterPercent=0
        // disables the random draw so unit tests that pin the exact
        // backoff curve do not flake.
        var policy = new RetryPolicy { JitterPercent = 0 };
        var rng = NewSeededRandom();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var expectedMs = policy.InitialDelayMs * Math.Pow(policy.BackoffMultiplier, attempt - 1);
            var delay = policy.ComputeDelay(attempt, rng);

            delay.TotalMilliseconds.Should().BeApproximately(
                expectedMs,
                precision: 0.001,
                $"attempt {attempt}: with JitterPercent=0 the delay must be exactly base * multiplier^(attempt-1)");
        }
    }

    [Fact]
    public void ComputeDelay_OverlargeJitterPercent_IsClampedTo_100()
    {
        // A misconfig with JitterPercent=999 must NOT inflate the
        // jitter band to ±999% — that would routinely produce
        // negative draws and a "delay" of 0 every time. The
        // implementation clamps to [0, 100].
        var policy = new RetryPolicy { JitterPercent = 999 };
        var rng = NewSeededRandom();

        for (var i = 0; i < 100; i++)
        {
            var delay = policy.ComputeDelay(attempt: 1, rng);
            // With JitterPercent clamped at 100, the band is
            // [0, 2 * InitialDelayMs] for attempt=1.
            delay.TotalMilliseconds.Should().BeInRange(
                0,
                policy.InitialDelayMs * 2,
                "JitterPercent must be clamped to 100 to keep the band physically meaningful");
        }
    }

    [Fact]
    public void ComputeDelay_NullRandom_Throws_ArgumentNullException()
    {
        var policy = new RetryPolicy();

        Action act = () => policy.ComputeDelay(attempt: 1, random: null!);

        act.Should().Throw<ArgumentNullException>(
            "the random source is required by the jitter math — passing null is a caller bug worth surfacing eagerly, not a silent zero-jitter fallback");
    }
}
