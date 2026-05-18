// -----------------------------------------------------------------------
// <copyright file="SlackInboundEventBufferOverflowPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Configuration;

using AgentSwarm.Messaging.Slack.Pipeline;

/// <summary>
/// Policy applied when the bounded
/// <see cref="ISlackInboundEventBuffer"/> reaches capacity. Selected
/// via <see cref="SlackInboundEventBufferOptions.OverflowPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 8.1 evaluator iter-2 item 4 -- structural fix. The previous
/// default of <see cref="DropOldest"/> meant a stalled
/// <see cref="IMessengerConnector.ReceiveAsync"/> poller could
/// permanently miss processed <see cref="HumanDecisionEvent"/>s
/// (the oldest, never-drained events were the first to be evicted
/// when new events arrived). The default is now
/// <see cref="DropNewest"/> so the OLDEST events are preserved and
/// the poller eventually drains them once it catches up. The audit
/// log (<c>slack_audit_entry</c>) holds the authoritative copy of
/// every event regardless of which policy is in effect, so a host
/// that prefers the legacy ring-buffer semantics can opt in via
/// configuration.
/// </para>
/// </remarks>
public enum SlackInboundEventBufferOverflowPolicy
{
    /// <summary>
    /// Drop the NEWEST incoming event when the buffer is full.
    /// Preserves the oldest never-drained events so a stalled poller
    /// eventually surfaces them after catching up. Default since
    /// Stage 8.1 iter-2.
    /// </summary>
    DropNewest = 0,

    /// <summary>
    /// Drop the OLDEST event in the buffer when a new event arrives
    /// and the buffer is full. Legacy ring-buffer semantics; can
    /// permanently hide processed events from a stalled poller and
    /// is therefore NOT the default.
    /// </summary>
    DropOldest = 1,

    /// <summary>
    /// Reject the incoming enqueue when the buffer is full --
    /// <see cref="ISlackInboundEventBuffer.Enqueue"/> increments the
    /// drop counter and returns without modifying the buffer. The
    /// caller (typically
    /// <c>BufferingAgentTaskServiceDecorator</c>) already swallows
    /// enqueue exceptions, so reject behaves identically to
    /// <see cref="DropNewest"/> from the caller's perspective; the
    /// only observable difference is in implementations that throw
    /// when full. The default
    /// <see cref="InMemorySlackInboundEventBuffer"/> treats Reject as
    /// equivalent to DropNewest and increments the drop counter.
    /// </summary>
    Reject = 2,
}
