using System.Diagnostics.Metrics;

namespace AgentSwarm.Messaging.Core.Tests;

/// <summary>
/// Verifies <see cref="OutboxMetrics"/> emits values on the canonical instruments
/// documented in <c>architecture.md</c> §8.1 — delivery latency histogram, deliveries
/// counter, dead-letter counter, and pending-count gauge.
/// </summary>
public sealed class OutboxMetricsTests
{
    [Fact]
    public void RecordDelivery_EmitsHistogramAndCounterSamples()
    {
        var options = new OutboxOptions { MeterName = $"test.{Guid.NewGuid():N}" };
        using var metrics = new OutboxMetrics(options);

        var histogramSamples = new List<double>();
        var counterSamples = new List<long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != options.MeterName)
            {
                return;
            }

            if (instrument.Name == OutboxMetrics.DeliveryDurationInstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }

            if (instrument.Name == OutboxMetrics.DeliveriesInstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, m, _, _) => histogramSamples.Add(m));
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => counterSamples.Add(m));
        listener.Start();

        metrics.RecordDelivery("teams", OutboxPayloadTypes.AgentQuestion, OutboxDispatchOutcome.Success, 123.45);

        listener.Dispose();

        Assert.Single(histogramSamples);
        Assert.Equal(123.45, histogramSamples[0]);
        Assert.Single(counterSamples);
        Assert.Equal(1, counterSamples[0]);
    }

    [Fact]
    public void RecordDeadLetter_IncrementsCounter()
    {
        var options = new OutboxOptions { MeterName = $"test.{Guid.NewGuid():N}" };
        using var metrics = new OutboxMetrics(options);

        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == options.MeterName &&
                instrument.Name == OutboxMetrics.DeadLettersInstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref observed, m));
        listener.Start();

        metrics.RecordDeadLetter("teams", OutboxPayloadTypes.MessengerMessage);
        metrics.RecordDeadLetter("teams", OutboxPayloadTypes.AgentQuestion);

        listener.Dispose();

        Assert.Equal(2, observed);
    }

    [Fact]
    public void SetPendingCount_ObservableGaugeReportsLatestValue()
    {
        var options = new OutboxOptions { MeterName = $"test.{Guid.NewGuid():N}" };
        using var metrics = new OutboxMetrics(options);

        long latest = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == options.MeterName &&
                instrument.Name == OutboxMetrics.PendingCountInstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => latest = m);
        listener.Start();

        metrics.SetPendingCount(17);
        listener.RecordObservableInstruments();

        Assert.Equal(17, latest);
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new OutboxMetrics(null!));
    }
}
