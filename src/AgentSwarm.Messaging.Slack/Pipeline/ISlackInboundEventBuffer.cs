// -----------------------------------------------------------------------
// <copyright file="ISlackInboundEventBuffer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Collections.Generic;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Connector-internal FIFO buffer holding processed inbound
/// <see cref="MessengerEvent"/>s that <see cref="SlackConnector.ReceiveAsync"/>
/// drains to satisfy the platform-neutral
/// <see cref="IMessengerConnector"/> polling contract.
/// </summary>
/// <remarks>
/// <para>
/// Stage 8.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (implementation step 4: <see cref="SlackConnector.ReceiveAsync"/>
/// "drain processed inbound events from the inbound pipeline and
/// return as <see cref="IReadOnlyList{T}"/> of
/// <see cref="MessengerEvent"/>").
/// </para>
/// <para>
/// The Slack inbound pipeline publishes typed
/// <see cref="HumanDecisionEvent"/>s to the orchestrator through
/// <see cref="IAgentTaskService.PublishDecisionAsync"/> (Stage 5.3
/// <see cref="SlackInteractionHandler"/>). A
/// <see cref="BufferingAgentTaskServiceDecorator"/> registered by
/// <c>AddSlackMessenger</c> taps that call and also pushes the event
/// into this buffer so a host that polls
/// <see cref="IMessengerConnector.ReceiveAsync"/> uniformly across
/// every registered connector observes Slack-originated decisions
/// without needing to subscribe to the orchestrator directly.
/// </para>
/// <para>
/// The buffer is intentionally bounded: a slow consumer must NOT
/// retain unbounded events in memory, and the inbound pipeline already
/// persists the authoritative copy of every event in the audit log
/// (see <c>SlackAuditEntry</c>) so dropped events remain recoverable
/// through the audit query API documented in <c>architecture.md §2.14</c>.
/// </para>
/// </remarks>
public interface ISlackInboundEventBuffer
{
    /// <summary>
    /// Appends a processed inbound event to the buffer in FIFO order.
    /// When the buffer is at capacity the behaviour depends on the
    /// configured
    /// <see cref="AgentSwarm.Messaging.Slack.Configuration.SlackInboundEventBufferOptions.OverflowPolicy"/>;
    /// the default (drop-newest) preserves the oldest never-drained
    /// events. An internal drop counter is incremented on each evicted
    /// or rejected event; implementations SHOULD surface that counter
    /// through a diagnostic accessor for observability.
    /// </summary>
    /// <param name="ev">The event to enqueue. Must not be null.</param>
    void Enqueue(MessengerEvent ev);

    /// <summary>
    /// Removes up to <paramref name="maxItems"/> events from the head
    /// of the buffer and returns them in the order they were
    /// enqueued. Returns an empty list when the buffer is empty -- the
    /// connector's <see cref="IMessengerConnector.ReceiveAsync"/>
    /// contract is non-blocking, so callers MUST be prepared to receive
    /// nothing on a given poll.
    /// </summary>
    /// <param name="maxItems">Upper bound on the number of events to
    /// drain. A value &lt;= 0 returns an empty list.</param>
    IReadOnlyList<MessengerEvent> Drain(int maxItems);

    /// <summary>
    /// Current count of events in the buffer. Surfaced for tests and
    /// health-check probes.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Total events dropped because the buffer was at capacity.
    /// Monotonically increasing; never resets. Surfaced so an operator
    /// can correlate a sustained delta with downstream pollers that
    /// have stalled.
    /// </summary>
    long Dropped { get; }
}
