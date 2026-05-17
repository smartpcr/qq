using System.Diagnostics.Metrics;

namespace AgentSwarm.Messaging.Core;

/// <summary>
/// OpenTelemetry-aligned instrumentation for <see cref="OutboxRetryEngine"/>. Publishes
/// the canonical signals listed in <c>architecture.md</c> §8.1 — the
/// <c>teams.card.delivery.duration_ms</c> histogram (used to compute the P95 budget per
/// §9), the <c>teams.outbox.pending_count</c> gauge, and the
/// <c>teams.outbox.deliveries</c> /
/// <c>teams.outbox.deadletters</c> counters. Hosts wire an OpenTelemetry exporter to the
/// configured <see cref="OutboxOptions.MeterName"/> meter and the dashboards/alerts in the
/// architecture light up without further glue.
/// </summary>
/// <remarks>
/// <para>
/// The histogram emits the <i>complete</i> delivery latency from dequeue (i.e. queue
/// pickup) through successful Bot Connector acknowledgement — that is the latency the
/// P95 budget in <c>architecture.md</c> §9 targets ("P95 card delivery under 3 seconds
/// <b>after queue pickup</b>"). Measuring at the dispatcher boundary (rather than at the
/// engine boundary which includes poll-wait time) keeps the signal aligned with the
/// budget definition.
/// </para>
/// <para>
/// Every instrument records the canonical attributes used by the architecture dashboards
/// (<c>messenger</c>, <c>payload_type</c>, <c>outcome</c>) so a single PromQL / KQL query
/// can slice the P95 by messenger and payload type.
/// </para>
/// </remarks>
public sealed class OutboxMetrics : IDisposable
{
    /// <summary>Canonical instrument name for the delivery latency histogram.</summary>
    public const string DeliveryDurationInstrumentName = "teams.card.delivery.duration_ms";

    /// <summary>Canonical instrument name for the pending-entries gauge.</summary>
    public const string PendingCountInstrumentName = "teams.outbox.pending_count";

    /// <summary>Canonical instrument name for the deliveries counter.</summary>
    public const string DeliveriesInstrumentName = "teams.outbox.deliveries";

    /// <summary>Canonical instrument name for the dead-letter counter.</summary>
    public const string DeadLettersInstrumentName = "teams.outbox.deadletters";

    private readonly Meter _meter;
    private readonly Histogram<double> _deliveryDurationMs;
    private readonly Counter<long> _deliveries;
    private readonly Counter<long> _deadLetters;
    private long _pendingCount;

    /// <summary>
    /// Construct the metrics surface bound to <see cref="OutboxOptions.MeterName"/>.
    /// </summary>
    /// <param name="options">Configured outbox options.</param>
    public OutboxMetrics(OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _meter = new Meter(options.MeterName);

        _deliveryDurationMs = _meter.CreateHistogram<double>(
            name: DeliveryDurationInstrumentName,
            unit: "ms",
            description: "End-to-end delivery latency from outbox dequeue through successful Bot Connector acknowledgement. P95 budget < 3 s per architecture.md §9.");

        _deliveries = _meter.CreateCounter<long>(
            name: DeliveriesInstrumentName,
            unit: "{message}",
            description: "Outbox delivery attempts grouped by outcome (success / transient / permanent).");

        _deadLetters = _meter.CreateCounter<long>(
            name: DeadLettersInstrumentName,
            unit: "{message}",
            description: "Outbox entries transitioned to DeadLettered after exhausting retries.");

        _meter.CreateObservableGauge(
            name: PendingCountInstrumentName,
            observeValue: () => Interlocked.Read(ref _pendingCount),
            unit: "{entry}",
            description: "Number of outbox entries last observed in Pending status.");
    }

    /// <summary>
    /// Record a delivery latency sample. <paramref name="messenger"/> is typically
    /// <c>"teams"</c>; <paramref name="payloadType"/> is one of
    /// <see cref="OutboxPayloadTypes.All"/>; <paramref name="outcome"/> is the
    /// dispatcher result.
    /// </summary>
    public void RecordDelivery(string messenger, string payloadType, OutboxDispatchOutcome outcome, double durationMs)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("messenger", messenger),
            new("payload_type", payloadType),
            new("outcome", outcome.ToString()),
        };
        _deliveryDurationMs.Record(durationMs, tags);
        _deliveries.Add(1, tags);
    }

    /// <summary>Record a dead-letter event.</summary>
    public void RecordDeadLetter(string messenger, string payloadType)
    {
        _deadLetters.Add(
            1,
            new KeyValuePair<string, object?>("messenger", messenger),
            new KeyValuePair<string, object?>("payload_type", payloadType));
    }

    /// <summary>
    /// Set the current pending-count observation surfaced by the gauge. Called by the
    /// engine after each poll so the gauge reflects the last observed depth without
    /// requiring a callback into the outbox.
    /// </summary>
    public void SetPendingCount(long value) => Interlocked.Exchange(ref _pendingCount, value);

    /// <summary>
    /// Stage 6.3 — read the last <see cref="SetPendingCount"/> value. Exposed so the
    /// Teams-side <c>OutboxMetricsQueueDepthProvider</c> can mirror the outbox-engine
    /// depth onto the <c>teams.outbox.queue_depth</c> gauge published by the Teams meter
    /// without duplicating the underlying counter.
    /// </summary>
    public long GetPendingCount() => Interlocked.Read(ref _pendingCount);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
