// -----------------------------------------------------------------------
// <copyright file="InMemorySlackInboundEventBufferTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Generic;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 8.1 iter-2 item 4 unit tests for the
/// <see cref="InMemorySlackInboundEventBuffer"/> overflow policies.
/// </summary>
/// <remarks>
/// <para>
/// The structural fix for iter-1's item-4 critique changes the default
/// overflow policy from <c>DropOldest</c> (which could permanently
/// hide oldest events from a stalled poller) to <c>DropNewest</c>
/// (which preserves the oldest events for eventual catch-up). These
/// tests pin both behaviours so a regression that flips the default
/// or breaks either branch surfaces here.
/// </para>
/// </remarks>
public sealed class InMemorySlackInboundEventBufferTests
{
    [Fact]
    public void Default_OverflowPolicy_is_DropNewest_preserving_oldest_events()
    {
        // Default options (no override) MUST use DropNewest so a
        // stalled poller eventually drains the events that have
        // been waiting longest. This is the structural fix called
        // out in iter-1 evaluator feedback item 4.
        SlackInboundEventBufferOptions opts = new() { Capacity = 3 };
        InMemorySlackInboundEventBuffer buffer = new(Options.Create(opts));

        HumanDecisionEvent[] events = MakeEvents(5, prefix: "drop-newest");
        foreach (HumanDecisionEvent ev in events)
        {
            buffer.Enqueue(ev);
        }

        // Capacity 3, 5 enqueues -> 2 dropped from the tail, the
        // first 3 events (the oldest) remain in FIFO order.
        buffer.Count.Should().Be(3);
        buffer.Dropped.Should().Be(2);

        IReadOnlyList<MessengerEvent> drained = buffer.Drain(int.MaxValue);
        drained.Should().HaveCount(3);
        ((HumanDecisionEvent)drained[0]).QuestionId.Should().Be("drop-newest-0");
        ((HumanDecisionEvent)drained[1]).QuestionId.Should().Be("drop-newest-1");
        ((HumanDecisionEvent)drained[2]).QuestionId.Should().Be("drop-newest-2");
    }

    [Fact]
    public void Reject_OverflowPolicy_behaves_identically_to_DropNewest()
    {
        // Reject is documented as equivalent to DropNewest for the
        // in-memory implementation (the caller already swallows
        // enqueue exceptions and Reject increments the drop counter
        // rather than throwing).
        SlackInboundEventBufferOptions opts = new()
        {
            Capacity = 2,
            OverflowPolicy = SlackInboundEventBufferOverflowPolicy.Reject,
        };
        InMemorySlackInboundEventBuffer buffer = new(Options.Create(opts));

        foreach (HumanDecisionEvent ev in MakeEvents(4, prefix: "reject"))
        {
            buffer.Enqueue(ev);
        }

        buffer.Count.Should().Be(2);
        buffer.Dropped.Should().Be(2);
        IReadOnlyList<MessengerEvent> drained = buffer.Drain(int.MaxValue);
        ((HumanDecisionEvent)drained[0]).QuestionId.Should().Be("reject-0");
        ((HumanDecisionEvent)drained[1]).QuestionId.Should().Be("reject-1");
    }

    [Fact]
    public void DropOldest_OverflowPolicy_evicts_head_to_make_room()
    {
        // Legacy ring-buffer behaviour for hosts that explicitly opt
        // in. Pinned here so a future refactor that silently changes
        // the semantics surfaces with a precise failure.
        SlackInboundEventBufferOptions opts = new()
        {
            Capacity = 3,
            OverflowPolicy = SlackInboundEventBufferOverflowPolicy.DropOldest,
        };
        InMemorySlackInboundEventBuffer buffer = new(Options.Create(opts));

        foreach (HumanDecisionEvent ev in MakeEvents(5, prefix: "drop-oldest"))
        {
            buffer.Enqueue(ev);
        }

        // Capacity 3, 5 enqueues -> 2 dropped from the head, the
        // last 3 events remain.
        buffer.Count.Should().Be(3);
        buffer.Dropped.Should().Be(2);

        IReadOnlyList<MessengerEvent> drained = buffer.Drain(int.MaxValue);
        drained.Should().HaveCount(3);
        ((HumanDecisionEvent)drained[0]).QuestionId.Should().Be("drop-oldest-2");
        ((HumanDecisionEvent)drained[1]).QuestionId.Should().Be("drop-oldest-3");
        ((HumanDecisionEvent)drained[2]).QuestionId.Should().Be("drop-oldest-4");
    }

    [Fact]
    public void Drop_counter_is_monotonic_across_sustained_overflow()
    {
        // The Dropped counter is surfaced for health-check probes;
        // it MUST be monotonically increasing across calls and MUST
        // NOT reset on Drain.
        SlackInboundEventBufferOptions opts = new() { Capacity = 1 };
        InMemorySlackInboundEventBuffer buffer = new(Options.Create(opts));

        foreach (HumanDecisionEvent ev in MakeEvents(3, prefix: "drop-counter"))
        {
            buffer.Enqueue(ev);
        }

        buffer.Dropped.Should().Be(2);

        // Drain everything (the surviving oldest event) and enqueue
        // more -- the counter does NOT reset.
        buffer.Drain(int.MaxValue);
        foreach (HumanDecisionEvent ev in MakeEvents(2, prefix: "drop-counter-second"))
        {
            buffer.Enqueue(ev);
        }

        buffer.Dropped.Should().Be(3, "Dropped is monotonic across drains");
    }

    [Fact]
    public void Non_positive_capacity_falls_back_to_DefaultCapacity()
    {
        SlackInboundEventBufferOptions opts = new() { Capacity = 0 };
        InMemorySlackInboundEventBuffer buffer = new(Options.Create(opts));

        // Enqueue a few events and confirm none are dropped -- the
        // default capacity (1024) easily accommodates this.
        for (int i = 0; i < 16; i++)
        {
            buffer.Enqueue(new HumanDecisionEvent(
                QuestionId: "Q-" + i,
                ActionValue: "approve",
                Comment: null,
                Messenger: "slack",
                ExternalUserId: "U-1",
                ExternalMessageId: "1700000000." + i.ToString("D6"),
                ReceivedAt: DateTimeOffset.UtcNow,
                CorrelationId: "corr-" + i));
        }

        buffer.Count.Should().Be(16);
        buffer.Dropped.Should().Be(0);
    }

    private static HumanDecisionEvent[] MakeEvents(int count, string prefix)
    {
        HumanDecisionEvent[] events = new HumanDecisionEvent[count];
        for (int i = 0; i < count; i++)
        {
            events[i] = new HumanDecisionEvent(
                QuestionId: prefix + "-" + i,
                ActionValue: "approve",
                Comment: null,
                Messenger: "slack",
                ExternalUserId: "U-" + i,
                ExternalMessageId: "1700000000." + i.ToString("D6"),
                ReceivedAt: DateTimeOffset.UtcNow,
                CorrelationId: "corr-" + i);
        }

        return events;
    }
}
