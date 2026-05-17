// -----------------------------------------------------------------------
// <copyright file="OutboundQueueOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Stage 4.1 — options bound from the <c>OutboundQueue</c>
/// configuration section. Drives the durable outbox's backpressure
/// threshold, the <c>OutboundQueueProcessor</c>'s worker fan-out, and
/// the dequeue poll cadence used when the queue is empty.
/// </summary>
/// <remarks>
/// <para>
/// Per architecture.md §10.4 the production defaults are
/// <see cref="ProcessorConcurrency"/> = 10 and
/// <see cref="MaxQueueDepth"/> = 5000. The dev override under
/// <c>appsettings.Development.json</c> tightens both to reduce
/// resource use on a laptop.
/// </para>
/// </remarks>
public sealed class OutboundQueueOptions
{
    /// <summary>
    /// Canonical configuration section name. Matches the
    /// <c>OutboundQueue</c> block already shipped in
    /// <c>appsettings.json</c> /
    /// <c>appsettings.Development.json</c>.
    /// </summary>
    public const string SectionName = "OutboundQueue";

    /// <summary>
    /// Canonical backpressure dead-letter reason string written to
    /// <see cref="Abstractions.OutboundMessage.ErrorDetail"/> when a
    /// <see cref="Abstractions.MessageSeverity.Low"/>-severity message
    /// is dead-lettered because the queue depth exceeded
    /// <see cref="MaxQueueDepth"/>. Architecture.md §10.4 pins the
    /// literal: a future audit query / metric label MUST be able to
    /// pivot on this exact value.
    /// </summary>
    public const string BackpressureDeadLetterReason = "backpressure:queue_depth_exceeded";

    /// <summary>
    /// Number of independent dequeue-and-send workers run by the
    /// Stage 4.1 <c>OutboundQueueProcessor</c>
    /// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>.
    /// Default 10 per architecture.md §10.4 — paired with the
    /// global 30 msg/s token-bucket and ~350 ms median HTTP RTT it
    /// gives the system enough fan-out to stay within the steady-
    /// state P95 ≤ 2 s SLO without exceeding the token budget.
    /// </summary>
    public int ProcessorConcurrency { get; set; } = 10;

    /// <summary>
    /// Backpressure threshold. When the durable queue's count of
    /// non-terminal rows
    /// (<see cref="Abstractions.OutboundMessageStatus.Pending"/> +
    /// <see cref="Abstractions.OutboundMessageStatus.Sending"/>)
    /// exceeds this depth, <c>EnqueueAsync</c> dead-letters incoming
    /// <see cref="Abstractions.MessageSeverity.Low"/>-severity messages
    /// immediately with reason
    /// <see cref="BackpressureDeadLetterReason"/> and increments the
    /// <c>telegram.messages.backpressure_dlq</c> counter.
    /// <see cref="Abstractions.MessageSeverity.Normal"/>,
    /// <see cref="Abstractions.MessageSeverity.High"/>, and
    /// <see cref="Abstractions.MessageSeverity.Critical"/> messages are
    /// always accepted regardless of depth (architecture.md §10.4).
    /// Default 5000.
    /// </summary>
    public int MaxQueueDepth { get; set; } = 5000;

    /// <summary>
    /// How long each worker idles between dequeue attempts when the
    /// last dequeue returned no work. Set short enough that an
    /// enqueue-to-send latency budget under steady state is dominated
    /// by HTTP RTT rather than poll cadence. Default 100 ms — leaves
    /// ~1.9 s of the P95 ≤ 2 s budget for queue dwell and HTTP RTT.
    /// </summary>
    public int DequeuePollIntervalMs { get; set; } = 100;

    /// <summary>
    /// Maximum send-attempt budget set on every freshly-enqueued
    /// outbox row when the caller does not supply an explicit
    /// <see cref="Abstractions.OutboundMessage.MaxAttempts"/>. Per
    /// architecture.md §5.3 the canonical default is 5; Stage 4.2
    /// (RetryPolicy) consumes the same value via the
    /// <c>OutboundQueue:MaxRetries</c> alias so both stages stay in
    /// lockstep.
    /// </summary>
    public int MaxRetries { get; set; } = 5;
}
