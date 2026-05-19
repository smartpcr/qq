using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentSwarm.Messaging.Teams.Diagnostics;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Unit tests for the Stage 6.3 telemetry surface — the
/// <see cref="TeamsConnectorTelemetry"/> <see cref="ActivitySource"/> and
/// <see cref="Meter"/> wiring. The tests subscribe to the canonical instrument
/// names with an <see cref="ActivityListener"/> / <see cref="MeterListener"/> just as
/// an OpenTelemetry exporter would, so a regression in the public source / meter
/// names is observable as a test failure.
/// </summary>
[Collection(TeamsTelemetryCollection.Name)]
public sealed class TeamsConnectorTelemetryTests
{
    [Fact]
    public void StartSendActivity_WithListener_StampsCanonicalAttributes()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TeamsConnectorTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        using var activity = telemetry.StartSendActivity(
            TeamsConnectorTelemetry.SendMessageActivityName,
            correlationId: "corr-stage63",
            messageType: TeamsConnectorTelemetry.MessageTypeMessengerMessage,
            destinationType: TeamsConnectorTelemetry.DestinationTypeConversation);

        Assert.NotNull(activity);
        Assert.Equal(TeamsConnectorTelemetry.SendMessageActivityName, activity!.OperationName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("corr-stage63", activity.GetTagItem(TeamsConnectorTelemetry.CorrelationIdTag));
        Assert.Equal(TeamsConnectorTelemetry.MessageTypeMessengerMessage, activity.GetTagItem(TeamsConnectorTelemetry.MessageTypeTag));
        Assert.Equal(TeamsConnectorTelemetry.DestinationTypeConversation, activity.GetTagItem(TeamsConnectorTelemetry.DestinationTypeTag));
    }

    [Fact]
    public void StartSendActivity_WithoutListener_ReturnsNullSilently()
    {
        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var activity = telemetry.StartSendActivity(
            TeamsConnectorTelemetry.SendQuestionActivityName,
            "corr-x",
            TeamsConnectorTelemetry.MessageTypeAgentQuestion,
            TeamsConnectorTelemetry.DestinationTypeUser);

        Assert.Null(activity);
    }

    [Fact]
    public void StartReceiveActivity_WithListener_TagsConsumerKindAndInboundDefaults()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TeamsConnectorTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        using var activity = telemetry.StartReceiveActivity("corr-receive");

        Assert.NotNull(activity);
        Assert.Equal(TeamsConnectorTelemetry.ReceiveActivityName, activity!.OperationName);
        Assert.Equal(ActivityKind.Consumer, activity.Kind);
        Assert.Equal("corr-receive", activity.GetTagItem(TeamsConnectorTelemetry.CorrelationIdTag));
        Assert.Equal(TeamsConnectorTelemetry.MessageTypeInboundEvent, activity.GetTagItem(TeamsConnectorTelemetry.MessageTypeTag));
        Assert.Equal(TeamsConnectorTelemetry.DestinationTypeInbound, activity.GetTagItem(TeamsConnectorTelemetry.DestinationTypeTag));
    }

    [Fact]
    public void RecordMessageSent_IncrementsMessagesSentCounter_WithCanonicalTags()
    {
        var observations = new List<(long Value, IDictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TeamsConnectorTelemetry.MeterName
                    && instrument.Name == TeamsConnectorTelemetry.MessagesSentInstrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            observations.Add((value, dict));
        });
        listener.Start();

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);
        telemetry.RecordMessageSent(
            correlationId: "corr-1",
            messageType: TeamsConnectorTelemetry.MessageTypeAgentQuestion,
            destinationType: TeamsConnectorTelemetry.DestinationTypeUser);

        var sample = Assert.Single(observations);
        Assert.Equal(1, sample.Value);
        Assert.Equal("corr-1", sample.Tags[TeamsConnectorTelemetry.CorrelationIdTag]);
        Assert.Equal(TeamsConnectorTelemetry.MessageTypeAgentQuestion, sample.Tags[TeamsConnectorTelemetry.MessageTypeTag]);
        Assert.Equal(TeamsConnectorTelemetry.DestinationTypeUser, sample.Tags[TeamsConnectorTelemetry.DestinationTypeTag]);
    }

    [Fact]
    public void RecordCardDeliveryDurationMs_RecordsHistogramSample()
    {
        var observations = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TeamsConnectorTelemetry.MeterName
                    && instrument.Name == TeamsConnectorTelemetry.CardDeliveryDurationInstrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => observations.Add(value));
        listener.Start();

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);
        for (var i = 1; i <= 5; i++)
        {
            telemetry.RecordCardDeliveryDurationMs(i * 10.0, "corr", TeamsConnectorTelemetry.MessageTypeMessengerMessage, TeamsConnectorTelemetry.DestinationTypeConversation);
        }

        Assert.Equal(new[] { 10.0, 20.0, 30.0, 40.0, 50.0 }, observations);
    }

    [Fact]
    public void QueueDepthGauge_ReadsCurrentValueFromProvider()
    {
        var provider = new InMemoryOutboxQueueDepthProvider();
        provider.SetQueueDepth(42);

        var observations = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TeamsConnectorTelemetry.MeterName
                    && instrument.Name == TeamsConnectorTelemetry.OutboxQueueDepthInstrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observations.Add(value));
        listener.Start();

        using var telemetry = new TeamsConnectorTelemetry(provider);

        // Force a collection pass — observable gauge values are not pushed; the listener
        // pulls them via RecordObservableInstruments().
        listener.RecordObservableInstruments();

        Assert.Single(observations);
        Assert.Equal(42L, observations[0]);

        provider.SetQueueDepth(7);
        observations.Clear();
        listener.RecordObservableInstruments();
        Assert.Single(observations);
        Assert.Equal(7L, observations[0]);
    }

    [Fact]
    public void Constructor_NullQueueDepthProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TeamsConnectorTelemetry(queueDepthProvider: null!));
    }
}
