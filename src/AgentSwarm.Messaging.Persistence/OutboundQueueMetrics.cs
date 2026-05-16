// -----------------------------------------------------------------------
// <copyright file="OutboundQueueMetrics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System.Diagnostics.Metrics;

/// <summary>
/// Stage 4.1 — single source-of-truth for the outbound queue's
/// metric instruments. Both <see cref="PersistentOutboundQueue"/>
/// (counter on backpressure dead-letter) and the
/// <c>OutboundQueueProcessor</c> (three latency histograms) emit
/// through this singleton so an OTEL / Prometheus exporter sees one
/// consistent meter rather than two competing instances with the same
/// name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical names (architecture.md §10.4).</b>
/// <list type="bullet">
///   <item><description>
///   <see cref="MeterName"/> = <c>AgentSwarm.Messaging.Outbound</c>
///   — the meter identifier OTEL / Prometheus exporters subscribe to.
///   </description></item>
///   <item><description>
///   <see cref="BackpressureDeadLetterCounter"/> name =
///   <c>telegram.messages.backpressure_dlq</c> (counter, unit
///   <c>messages</c>) — emitted by
///   <see cref="PersistentOutboundQueue.EnqueueAsync(Abstractions.OutboundMessage, System.Threading.CancellationToken)"/>
///   when a Low-severity message is dead-lettered because the queue
///   exceeds <see cref="OutboundQueueOptions.MaxQueueDepth"/>.
///   </description></item>
///   <item><description>
///   <see cref="FirstAttemptLatencyMs"/> name =
///   <c>telegram.send.first_attempt_latency_ms</c> (histogram, unit
///   <c>ms</c>) — the acceptance gate per architecture.md §10.4.
///   Emitted by the processor for first-attempt successful sends
///   that did not encounter Telegram 429 responses; local
///   token-bucket wait is included (architecture.md §10.4 lines
///   1168, 1216, 1234).
///   </description></item>
///   <item><description>
///   <see cref="AllAttemptsLatencyMs"/> name =
///   <c>telegram.send.all_attempts_latency_ms</c> (histogram, unit
///   <c>ms</c>) — all successful sends regardless of attempt
///   number or rate-limit holds (capacity planning).
///   </description></item>
///   <item><description>
///   <see cref="QueueDwellMs"/> name =
///   <c>telegram.send.queue_dwell_ms</c> (histogram, unit
///   <c>ms</c>) — enqueue-to-dequeue interval; queue backlog
///   diagnostic.
///   </description></item>
/// </list>
/// All histograms measure elapsed milliseconds; the
/// <see cref="OutboundQueueProcessor"/> derives them from
/// <see cref="Abstractions.OutboundMessage.CreatedAt"/> per
/// architecture.md §10.4 (the canonical "enqueue instant" anchor for
/// the P95 ≤ 2 s SLO).
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton in
/// <see cref="ServiceCollectionExtensions.AddMessagingPersistence"/>;
/// the underlying <see cref="System.Diagnostics.Metrics.Meter"/> is
/// process-local and disposed by the singleton's
/// <see cref="System.IDisposable"/> contract on host shutdown.
/// </para>
/// </remarks>
public sealed class OutboundQueueMetrics : System.IDisposable
{
    /// <summary>OTEL meter identifier.</summary>
    public const string MeterName = "AgentSwarm.Messaging.Outbound";

    /// <summary>Architecture.md §10.4 canonical counter name.</summary>
    public const string BackpressureDeadLetterCounterName = "telegram.messages.backpressure_dlq";

    /// <summary>Architecture.md §10.4 canonical histogram name (acceptance gate).</summary>
    public const string FirstAttemptLatencyMsName = "telegram.send.first_attempt_latency_ms";

    /// <summary>Architecture.md §10.4 canonical histogram name (capacity planning).</summary>
    public const string AllAttemptsLatencyMsName = "telegram.send.all_attempts_latency_ms";

    /// <summary>Architecture.md §10.4 canonical histogram name (queue dwell diagnostic).</summary>
    public const string QueueDwellMsName = "telegram.send.queue_dwell_ms";

    private readonly Meter _meter;

    /// <summary>
    /// Iter-3 evaluator item 4 — exposes the underlying
    /// <see cref="Meter"/> instance so MeterListener consumers
    /// (notably the test HistogramCollector) can filter by
    /// reference equality instead of by <see cref="MeterName"/>
    /// string. Multiple OutboundQueueMetrics instances in the same
    /// process all share the meter NAME, so name-based filtering
    /// cross-pollinates samples between parallel xUnit test
    /// classes; reference-based filtering scopes each collector
    /// to exactly one metrics instance and eliminates the flake
    /// observed on the OutboundQueueProcessorTests.QueueDwell test.
    /// </summary>
    public Meter Meter => _meter;

    public OutboundQueueMetrics()
    {
        _meter = new Meter(MeterName);
        BackpressureDeadLetterCounter = _meter.CreateCounter<long>(
            BackpressureDeadLetterCounterName,
            unit: "messages",
            description: "Count of outbound messages dead-lettered immediately on EnqueueAsync because the queue depth exceeded MaxQueueDepth — Low severity only, per architecture.md §10.4.");

        FirstAttemptLatencyMs = _meter.CreateHistogram<double>(
            FirstAttemptLatencyMsName,
            unit: "ms",
            description: "Acceptance-gate latency: enqueue (OutboundMessage.CreatedAt) → Telegram HTTP 200 for first-attempt sends that did not receive a 429; includes local token-bucket wait. P95 ≤ 2s SLO applies.");

        AllAttemptsLatencyMs = _meter.CreateHistogram<double>(
            AllAttemptsLatencyMsName,
            unit: "ms",
            description: "Capacity-planning latency: enqueue → Telegram HTTP 200 for every successful send, regardless of attempt count or 429 holds.");

        QueueDwellMs = _meter.CreateHistogram<double>(
            QueueDwellMsName,
            unit: "ms",
            description: "Diagnostic: enqueue → dequeue interval. Tracks queue backlog under burst.");
    }

    /// <summary>
    /// Counter emitted whenever a Low-severity message is
    /// dead-lettered at <c>EnqueueAsync</c> time because the queue
    /// depth exceeded the backpressure threshold. Increment is unit
    /// 1 per dead-lettered message; tag with <c>severity</c> so
    /// future non-Low backpressure modes (currently disallowed) are
    /// distinguishable on dashboards.
    /// </summary>
    public Counter<long> BackpressureDeadLetterCounter { get; }

    /// <summary>
    /// Acceptance-gate histogram per architecture.md §10.4.
    /// Recorded by <c>OutboundQueueProcessor</c> on every
    /// first-attempt send that succeeded without hitting a Telegram
    /// 429. The <c>P95 ≤ 2 s</c> story SLO applies to this metric.
    /// </summary>
    public Histogram<double> FirstAttemptLatencyMs { get; }

    /// <summary>
    /// Capacity-planning histogram per architecture.md §10.4.
    /// Recorded on every successful send regardless of attempt
    /// count or 429 wait.
    /// </summary>
    public Histogram<double> AllAttemptsLatencyMs { get; }

    /// <summary>
    /// Diagnostic histogram per architecture.md §10.4. Recorded on
    /// every successful dequeue so operators can observe queue
    /// backlog directly without inferring it from the send-latency
    /// distributions.
    /// </summary>
    public Histogram<double> QueueDwellMs { get; }

    public void Dispose()
    {
        _meter.Dispose();
    }
}
