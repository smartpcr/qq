// -----------------------------------------------------------------------
// <copyright file="SlackInboundEventBufferOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Configuration;

using AgentSwarm.Messaging.Slack.Pipeline;

/// <summary>
/// Strongly-typed options for the connector-internal
/// <see cref="ISlackInboundEventBuffer"/> that backs
/// <see cref="SlackConnector.ReceiveAsync"/>. Bound by
/// <c>AddSlackMessenger</c> from the
/// <see cref="SectionName"/> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Stage 8.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The buffer is bounded so a stalled
/// <see cref="IMessengerConnector.ReceiveAsync"/> poller cannot
/// retain unbounded events in memory; the audit log holds the
/// authoritative copy of every event so dropping under back-pressure
/// is recoverable through the audit query API
/// (<c>architecture.md §2.14</c>).
/// </para>
/// <para>
/// <b>Iter-2 item 4 -- overflow policy.</b>
/// <see cref="OverflowPolicy"/> defaults to
/// <see cref="SlackInboundEventBufferOverflowPolicy.DropNewest"/> so
/// the OLDEST events are preserved when the buffer is full. The
/// previous Stage 8.1 iter-1 default (drop-oldest) could permanently
/// hide processed events from a stalled poller; drop-newest ensures
/// the poller eventually catches up by draining the events that have
/// been waiting longest. Hosts that prefer the legacy ring-buffer
/// semantics can set this to
/// <see cref="SlackInboundEventBufferOverflowPolicy.DropOldest"/>
/// explicitly.
/// </para>
/// </remarks>
public sealed class SlackInboundEventBufferOptions
{
    /// <summary>
    /// Configuration section name bound by
    /// <c>AddSlackMessenger</c>: <c>"Slack:InboundEventBuffer"</c>.
    /// </summary>
    public const string SectionName = "Slack:InboundEventBuffer";

    /// <summary>
    /// Maximum number of buffered <see cref="MessengerEvent"/>s
    /// retained before the
    /// <see cref="OverflowPolicy"/> kicks in. Defaults
    /// to <see cref="InMemorySlackInboundEventBuffer.DefaultCapacity"/>.
    /// A value &lt;= 0 falls back to the default.
    /// </summary>
    public int Capacity { get; set; } = InMemorySlackInboundEventBuffer.DefaultCapacity;

    /// <summary>
    /// Policy applied when <see cref="ISlackInboundEventBuffer.Enqueue"/>
    /// is called while the buffer is at capacity. Defaults to
    /// <see cref="SlackInboundEventBufferOverflowPolicy.DropNewest"/>
    /// so the oldest never-drained events survive (Stage 8.1 iter-2
    /// item 4 structural fix). Override to
    /// <see cref="SlackInboundEventBufferOverflowPolicy.DropOldest"/>
    /// only if the host explicitly prefers a freshness-first policy.
    /// </summary>
    public SlackInboundEventBufferOverflowPolicy OverflowPolicy { get; set; }
        = SlackInboundEventBufferOverflowPolicy.DropNewest;
}
