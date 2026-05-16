// -----------------------------------------------------------------------
// <copyright file="DefaultSlackRetryPolicyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 4.3 tests for <see cref="DefaultSlackRetryPolicy"/>.
/// </summary>
public sealed class DefaultSlackRetryPolicyTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void ShouldRetry_caps_at_max_attempts(int attempt, bool expected)
    {
        DefaultSlackRetryPolicy policy = BuildPolicy(maxAttempts: 5);

        policy.ShouldRetry(attempt, new InvalidOperationException("transient")).Should().Be(expected);
    }

    [Fact]
    public void ShouldRetry_returns_false_for_operation_cancelled()
    {
        DefaultSlackRetryPolicy policy = BuildPolicy(maxAttempts: 5);

        policy.ShouldRetry(1, new OperationCanceledException()).Should().BeFalse(
            "cancellation is shutdown -- retrying would block clean shutdown");
    }

    [Fact]
    public void ShouldRetry_rejects_zero_or_negative_attempt_numbers()
    {
        DefaultSlackRetryPolicy policy = BuildPolicy(maxAttempts: 5);

        policy.ShouldRetry(0, new InvalidOperationException()).Should().BeFalse();
        policy.ShouldRetry(-1, new InvalidOperationException()).Should().BeFalse();
    }

    [Fact]
    public void GetDelay_grows_exponentially_until_max_cap()
    {
        DefaultSlackRetryPolicy policy = BuildPolicy(maxAttempts: 10, initialDelayMs: 100, maxDelaySeconds: 2);

        policy.GetDelay(1).TotalMilliseconds.Should().Be(100);
        policy.GetDelay(2).TotalMilliseconds.Should().Be(200);
        policy.GetDelay(3).TotalMilliseconds.Should().Be(400);
        policy.GetDelay(4).TotalMilliseconds.Should().Be(800);
        policy.GetDelay(5).TotalMilliseconds.Should().Be(1600);
        policy.GetDelay(6).TotalMilliseconds.Should().Be(2000, "capped at 2 seconds");
        policy.GetDelay(20).TotalMilliseconds.Should().Be(2000, "cap holds regardless of attempt #");
    }

    [Fact]
    public void GetDelay_returns_zero_for_zero_or_negative_attempt()
    {
        DefaultSlackRetryPolicy policy = BuildPolicy(maxAttempts: 5);

        policy.GetDelay(0).Should().Be(TimeSpan.Zero);
        policy.GetDelay(-1).Should().Be(TimeSpan.Zero);
    }

    private static DefaultSlackRetryPolicy BuildPolicy(
        int maxAttempts,
        int initialDelayMs = 200,
        int maxDelaySeconds = 30)
    {
        SlackConnectorOptions opts = new()
        {
            Retry = new SlackRetryOptions
            {
                MaxAttempts = maxAttempts,
                InitialDelayMilliseconds = initialDelayMs,
                MaxDelaySeconds = maxDelaySeconds,
            },
        };

        return new DefaultSlackRetryPolicy(new TestOptionsMonitor<SlackConnectorOptions>(opts));
    }

    private sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TestOptionsMonitor(TOptions value)
        {
            this.CurrentValue = value;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => this.CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
