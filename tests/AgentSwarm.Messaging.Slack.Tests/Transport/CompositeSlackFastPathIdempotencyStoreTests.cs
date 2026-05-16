// -----------------------------------------------------------------------
// <copyright file="CompositeSlackFastPathIdempotencyStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.1 iter-3 evaluator item 2 unit tests for
/// <see cref="CompositeSlackFastPathIdempotencyStore"/>. Pins the
/// L1-first/L2-second composition contract, the graceful-degradation
/// path when the L2 reports
/// <see cref="SlackFastPathIdempotencyOutcome.StoreUnavailable"/>, and
/// the release-propagation contract on the failure leg.
/// </summary>
public sealed class CompositeSlackFastPathIdempotencyStoreTests
{
    [Fact]
    public async Task First_call_acquires_both_levels_and_returns_Acquired()
    {
        SlackInProcessIdempotencyStore l1 = new();
        RecordingL2 l2 = new() { Plan = SlackFastPathIdempotencyResult.Acquired() };
        CompositeSlackFastPathIdempotencyStore composite = new(l1, l2, NullLogger<CompositeSlackFastPathIdempotencyStore>.Instance);

        SlackFastPathIdempotencyResult result = await composite
            .TryAcquireAsync("key-1", BuildEnvelope("key-1"), lifetime: null, ct: CancellationToken.None);

        result.Outcome.Should().Be(SlackFastPathIdempotencyOutcome.Acquired);
        l2.AcquireInvocations.Should().Be(1);
        l1.LiveCount.Should().Be(1, "L1 holds the token until release");
    }

    [Fact]
    public async Task Second_call_is_short_circuited_by_L1_without_hitting_L2()
    {
        SlackInProcessIdempotencyStore l1 = new();
        RecordingL2 l2 = new() { Plan = SlackFastPathIdempotencyResult.Acquired() };
        CompositeSlackFastPathIdempotencyStore composite = new(l1, l2, NullLogger<CompositeSlackFastPathIdempotencyStore>.Instance);

        SlackInboundEnvelope envelope = BuildEnvelope("key-2");
        await composite.TryAcquireAsync("key-2", envelope, lifetime: null, ct: CancellationToken.None);

        SlackFastPathIdempotencyResult second = await composite
            .TryAcquireAsync("key-2", envelope, lifetime: null, ct: CancellationToken.None);

        second.Outcome.Should().Be(SlackFastPathIdempotencyOutcome.Duplicate);
        l2.AcquireInvocations.Should().Be(1,
            "L1 catches the duplicate before the DB round-trip -- L2 must NOT be called twice");
    }

    [Fact]
    public async Task L2_duplicate_overrides_L1_acquire_and_releases_the_L1_token()
    {
        SlackInProcessIdempotencyStore l1 = new();
        RecordingL2 l2 = new() { Plan = SlackFastPathIdempotencyResult.Duplicate("another replica got there first") };
        CompositeSlackFastPathIdempotencyStore composite = new(l1, l2, NullLogger<CompositeSlackFastPathIdempotencyStore>.Instance);

        SlackFastPathIdempotencyResult result = await composite
            .TryAcquireAsync("key-3", BuildEnvelope("key-3"), lifetime: null, ct: CancellationToken.None);

        result.Outcome.Should().Be(SlackFastPathIdempotencyOutcome.Duplicate);
        result.Diagnostic.Should().Contain("another replica");
        l1.LiveCount.Should().Be(0,
            "L1 token must be released so a future retry can proceed once the L2 row expires");
    }

    [Fact]
    public async Task L2_StoreUnavailable_degrades_to_L1_only_and_still_returns_Acquired()
    {
        SlackInProcessIdempotencyStore l1 = new();
        RecordingL2 l2 = new() { Plan = SlackFastPathIdempotencyResult.StoreUnavailable("db blip") };
        CompositeSlackFastPathIdempotencyStore composite = new(l1, l2, NullLogger<CompositeSlackFastPathIdempotencyStore>.Instance);

        SlackFastPathIdempotencyResult result = await composite
            .TryAcquireAsync("key-4", BuildEnvelope("key-4"), lifetime: null, ct: CancellationToken.None);

        result.Outcome.Should().Be(SlackFastPathIdempotencyOutcome.Acquired,
            "failing every modal during a DB blip is worse than degrading to L1-only dedup");
        l1.LiveCount.Should().Be(1);
    }

    [Fact]
    public async Task ReleaseAsync_releases_both_levels()
    {
        SlackInProcessIdempotencyStore l1 = new();
        RecordingL2 l2 = new() { Plan = SlackFastPathIdempotencyResult.Acquired() };
        CompositeSlackFastPathIdempotencyStore composite = new(l1, l2, NullLogger<CompositeSlackFastPathIdempotencyStore>.Instance);

        SlackInboundEnvelope envelope = BuildEnvelope("key-5");
        await composite.TryAcquireAsync("key-5", envelope, lifetime: null, ct: CancellationToken.None);

        await composite.ReleaseAsync("key-5", CancellationToken.None);

        l1.LiveCount.Should().Be(0);
        l2.ReleasedKeys.Should().ContainSingle().Which.Should().Be("key-5");
    }

    [Fact]
    public async Task L2_exception_releases_the_L1_token_and_rethrows()
    {
        SlackInProcessIdempotencyStore l1 = new();
        ThrowingL2 l2 = new();
        CompositeSlackFastPathIdempotencyStore composite = new(l1, l2, NullLogger<CompositeSlackFastPathIdempotencyStore>.Instance);

        Func<Task> act = () => composite
            .TryAcquireAsync("key-6", BuildEnvelope("key-6"), lifetime: null, ct: CancellationToken.None)
            .AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        l1.LiveCount.Should().Be(0, "the L1 token must be released so a retry isn't blocked by the leak");
    }

    private static SlackInboundEnvelope BuildEnvelope(string idempotencyKey)
    {
        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T-test",
            ChannelId: "C-test",
            UserId: "U-test",
            RawPayload: "team_id=T-test&user_id=U-test&command=/agent&trigger_id=trig-test",
            TriggerId: "trig-test",
            ReceivedAt: DateTimeOffset.UtcNow);
    }

    private sealed class RecordingL2 : ISlackFastPathIdempotencyStore
    {
        public SlackFastPathIdempotencyResult Plan { get; set; } = SlackFastPathIdempotencyResult.Acquired();

        public int AcquireInvocations { get; private set; }

        public List<string> ReleasedKeys { get; } = new();

        public ValueTask<SlackFastPathIdempotencyResult> TryAcquireAsync(
            string key,
            SlackInboundEnvelope envelope,
            TimeSpan? lifetime = null,
            CancellationToken ct = default)
        {
            this.AcquireInvocations++;
            return new ValueTask<SlackFastPathIdempotencyResult>(this.Plan);
        }

        public ValueTask ReleaseAsync(string key, CancellationToken ct = default)
        {
            this.ReleasedKeys.Add(key);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingL2 : ISlackFastPathIdempotencyStore
    {
        public ValueTask<SlackFastPathIdempotencyResult> TryAcquireAsync(
            string key,
            SlackInboundEnvelope envelope,
            TimeSpan? lifetime = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("L2 blew up");

        public ValueTask ReleaseAsync(string key, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}
