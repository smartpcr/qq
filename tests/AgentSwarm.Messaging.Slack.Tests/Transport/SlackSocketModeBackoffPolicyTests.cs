// -----------------------------------------------------------------------
// <copyright file="SlackSocketModeBackoffPolicyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

/// <summary>
/// Verifies the <see cref="SlackSocketModeBackoffPolicy"/> exponential
/// backoff schedule and full-jitter envelope per Stage 4.2 of
/// <c>implementation-plan.md</c>: "exponential backoff (initial 1s,
/// max 30s) and jitter on WebSocket disconnection".
/// </summary>
public sealed class SlackSocketModeBackoffPolicyTests
{
    [Fact]
    public void Ceiling_doubles_until_max_then_caps()
    {
        SlackSocketModeBackoffPolicy policy = new(new SlackSocketModeOptions
        {
            InitialReconnectDelay = TimeSpan.FromSeconds(1),
            MaxReconnectDelay = TimeSpan.FromSeconds(30),
        });

        policy.ComputeCeiling(1).Should().Be(TimeSpan.FromSeconds(1),
            "first reconnect attempt uses the base delay");
        policy.ComputeCeiling(2).Should().Be(TimeSpan.FromSeconds(2));
        policy.ComputeCeiling(3).Should().Be(TimeSpan.FromSeconds(4));
        policy.ComputeCeiling(4).Should().Be(TimeSpan.FromSeconds(8));
        policy.ComputeCeiling(5).Should().Be(TimeSpan.FromSeconds(16));
        policy.ComputeCeiling(6).Should().Be(TimeSpan.FromSeconds(30),
            "the cap is 30 seconds per architecture.md §2.2.3");
        policy.ComputeCeiling(100).Should().Be(TimeSpan.FromSeconds(30),
            "very high attempt counters stay clamped at the configured max");
    }

    [Fact]
    public void Delay_is_always_within_jitter_envelope()
    {
        SlackSocketModeBackoffPolicy policy = new(new SlackSocketModeOptions
        {
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaxReconnectDelay = TimeSpan.FromSeconds(5),
        });

        // Deterministic random seed so failures reproduce.
        Random rng = new(Seed: 1234);
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            TimeSpan ceiling = policy.ComputeCeiling(attempt);
            for (int i = 0; i < 50; i++)
            {
                TimeSpan delay = policy.ComputeDelay(attempt, rng);
                delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
                delay.Should().BeLessThanOrEqualTo(ceiling,
                    $"full-jitter delay for attempt {attempt} must stay within ceiling {ceiling}");
            }
        }
    }

    [Fact]
    public void Attempts_less_than_one_are_treated_as_one()
    {
        SlackSocketModeBackoffPolicy policy = new(new SlackSocketModeOptions
        {
            InitialReconnectDelay = TimeSpan.FromMilliseconds(500),
            MaxReconnectDelay = TimeSpan.FromSeconds(10),
        });

        policy.ComputeCeiling(0).Should().Be(TimeSpan.FromMilliseconds(500));
        policy.ComputeCeiling(-5).Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction()
    {
        Action zeroInitial = () => new SlackSocketModeBackoffPolicy(new SlackSocketModeOptions
        {
            InitialReconnectDelay = TimeSpan.Zero,
            MaxReconnectDelay = TimeSpan.FromSeconds(30),
        });
        zeroInitial.Should().Throw<ArgumentException>();

        Action maxLessThanInitial = () => new SlackSocketModeBackoffPolicy(new SlackSocketModeOptions
        {
            InitialReconnectDelay = TimeSpan.FromSeconds(5),
            MaxReconnectDelay = TimeSpan.FromSeconds(1),
        });
        maxLessThanInitial.Should().Throw<ArgumentException>();
    }
}
