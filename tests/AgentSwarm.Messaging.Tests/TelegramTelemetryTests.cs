// -----------------------------------------------------------------------
// <copyright file="TelegramTelemetryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using AgentSwarm.Messaging.Telegram.Diagnostics;
using FluentAssertions;

/// <summary>
/// Stage 6.1 — pins for <see cref="TelegramTelemetry"/>: the
/// canonical <see cref="ActivitySource"/> name, the canonical
/// <see cref="Meter"/> name, the canonical instrument names emitted
/// by the Telegram pipeline / connector / sender, and the span
/// helpers (<c>StartReceiveSpan</c>, <c>StartCommandSpan</c>,
/// <c>StartSendSpan</c>, <c>StartQueueDrainSpan</c>) the workstream
/// requires to instrument all command processing, outbound sends,
/// and queue operations.
/// </summary>
public sealed class TelegramTelemetryTests
{
    // ============================================================
    // Stage 6.1 brief — canonical identifiers
    // ============================================================

    [Fact]
    public void ActivitySourceName_MatchesArchitectureSpec()
    {
        // architecture.md §8 + Stage 6.1 acceptance scenario: the
        // span must be tagged with
        // ActivitySource=AgentSwarm.Messaging.Telegram. This is the
        // string an OTEL ActivityListener subscribes to; if the name
        // drifts, the exporter receives nothing and the scenario
        // silently fails.
        TelegramTelemetry.ActivitySourceName.Should().Be("AgentSwarm.Messaging.Telegram");
        TelegramTelemetry.Source.Name.Should().Be("AgentSwarm.Messaging.Telegram");
    }

    [Fact]
    public void MeterName_MatchesArchitectureSpec()
    {
        TelegramTelemetry.MeterName.Should().Be("AgentSwarm.Messaging.Telegram");
        TelegramTelemetry.Meter.Name.Should().Be("AgentSwarm.Messaging.Telegram");
    }

    [Theory]
    [InlineData("telegram.messages.received")]
    [InlineData("telegram.messages.sent")]
    [InlineData("telegram.commands.processed")]
    [InlineData("telegram.errors")]
    [InlineData("telegram.messages.dead_lettered")]
    public void Counter_HasCanonicalName(string expectedName)
    {
        // Stage 6.1 brief: "create custom meters for
        // telegram.messages.sent, telegram.messages.received,
        // telegram.commands.processed, telegram.errors,
        // telegram.queue.depth, telegram.dlq.depth". The counter
        // names are part of the public observability contract; a
        // rename here would silently invalidate every dashboard
        // panel keyed on the old name.
        //
        // Iter-2 evaluator item 5 — implementation-plan Stage 6.1
        // ALSO requires `telegram.messages.dead_lettered` as a
        // canonical counter, distinct from the persistence-meter's
        // `telegram.messages.backpressure_dlq` (which counts
        // enqueue-time MaxQueueDepth drops). The runtime DLQ counter
        // is emitted by OutboundQueueProcessor after the audit
        // ledger row is durably written.
        var instrument = ResolveInstrument(expectedName);
        instrument.Should().NotBeNull(
            $"the Stage 6.1 brief requires a counter named '{expectedName}' on the AgentSwarm.Messaging.Telegram meter");
    }

    [Theory]
    [InlineData("telegram.send.rate_limited_wait_ms")]
    public void Histogram_HasCanonicalName(string expectedName)
    {
        // Iter-2 evaluator item 5 — implementation-plan Stage 6.1
        // requires `telegram.send.rate_limited_wait_ms` as a
        // diagnostic histogram emitted on every Telegram 429
        // retry_after wait. The instrument lives on the canonical
        // connector meter (AgentSwarm.Messaging.Telegram) so an
        // operator subscribing to ONE meter sees the entire
        // connector signal; `telegram.send.retry_latency_ms` lives
        // on the persistence meter (AgentSwarm.Messaging.Outbound)
        // because the processor is the natural emitter and owns the
        // other latency histograms there.
        var instrument = ResolveInstrument(expectedName);
        instrument.Should().NotBeNull(
            $"the Stage 6.1 brief / implementation-plan requires a histogram named '{expectedName}' on the AgentSwarm.Messaging.Telegram meter");
    }

    [Theory]
    [InlineData("telegram.queue.depth")]
    [InlineData("telegram.dlq.depth")]
    public void GaugeNameConstants_MatchBrief(string expectedName)
    {
        var constantNames = new[]
        {
            TelegramTelemetry.QueueDepthGaugeName,
            TelegramTelemetry.DlqDepthGaugeName,
        };

        constantNames.Should().Contain(expectedName,
            $"the Stage 6.1 brief requires the gauge name '{expectedName}' (registered by TelegramQueueDepthMetrics)");
    }

    // ============================================================
    // Span helpers — the "instrument command processing, outbound
    // sends, and queue operations" deliverable.
    // ============================================================

    [Fact]
    public void StartCommandSpan_TagsCorrelationId_AndOperatorIdentifiers()
    {
        // Iter-3 — filter the listener by THIS test's correlation id
        // so xUnit's class-level parallelism (sibling tests creating
        // spans on the same AgentSwarm.Messaging.Telegram source) does
        // not poison the ContainSingle assertion below.
        const string correlationId = "trace-abc-command-tagcheck";
        using var listener = new SpanCollector(
            TelegramTelemetry.ActivitySourceName,
            a => string.Equals(
                a.GetTagItem(TelegramTelemetry.CorrelationIdKey) as string,
                correlationId,
                StringComparison.Ordinal));

        using (var activity = TelegramTelemetry.StartCommandSpan(
            correlationId: correlationId,
            commandName: "/status",
            telegramUserId: 12345,
            telegramChatId: 67890,
            eventId: 42))
        {
            activity.Should().NotBeNull(
                "an ActivityListener that AllData-samples must materialize the activity");
        }

        var span = listener.Spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be(TelegramTelemetry.CommandActivityName);
        TagValue<string>(span, TelegramTelemetry.CorrelationIdKey).Should().Be(correlationId,
            "Stage 6.1 acceptance scenario #1 hinges on this tag");
        TagValue<long>(span, TelegramTelemetry.TelegramUserIdKey).Should().Be(12345);
        TagValue<long>(span, TelegramTelemetry.TelegramChatIdKey).Should().Be(67890);
        TagValue<string>(span, TelegramTelemetry.CommandNameKey).Should().Be("/status");
        TagValue<long>(span, TelegramTelemetry.EventIdKey).Should().Be(42);
    }

    [Fact]
    public void StartSendSpan_TagsSourceType_AndCorrelationId()
    {
        // Iter-3 — correlation-id-scoped listener (see StartCommandSpan
        // test for the parallelism rationale).
        const string correlationId = "trace-send-tagcheck";
        using var listener = new SpanCollector(
            TelegramTelemetry.ActivitySourceName,
            a => string.Equals(
                a.GetTagItem(TelegramTelemetry.CorrelationIdKey) as string,
                correlationId,
                StringComparison.Ordinal));

        using (TelegramTelemetry.StartSendSpan(correlationId: correlationId, chatId: 100, sourceType: "question"))
        {
            // Activity scope intentionally empty.
        }

        var span = listener.Spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be(TelegramTelemetry.SendActivityName);
        span.Kind.Should().Be(ActivityKind.Client,
            "outbound sends are Client-kind spans so the trace tree shows the bot as the caller");
        TagValue<string>(span, TelegramTelemetry.CorrelationIdKey).Should().Be(correlationId);
        TagValue<string>(span, TelegramTelemetry.OutboundSourceKey).Should().Be("question");
        TagValue<long>(span, TelegramTelemetry.TelegramChatIdKey).Should().Be(100);
    }

    [Fact]
    public void StartReceiveSpan_TagsCorrelationId_AndEventMetadata()
    {
        // Iter-3 — correlation-id-scoped listener (see StartCommandSpan
        // test for the parallelism rationale).
        const string correlationId = "trace-recv-tagcheck";
        using var listener = new SpanCollector(
            TelegramTelemetry.ActivitySourceName,
            a => string.Equals(
                a.GetTagItem(TelegramTelemetry.CorrelationIdKey) as string,
                correlationId,
                StringComparison.Ordinal));

        using (TelegramTelemetry.StartReceiveSpan(correlationId: correlationId, eventId: 7, eventType: "command"))
        {
        }

        var span = listener.Spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be(TelegramTelemetry.ReceiveActivityName);
        span.Kind.Should().Be(ActivityKind.Server,
            "the webhook receive span MUST be Server-kind so end-to-end traces show the request entry point");
        TagValue<string>(span, TelegramTelemetry.CorrelationIdKey).Should().Be(correlationId);
        TagValue<long>(span, TelegramTelemetry.EventIdKey).Should().Be(7);
        TagValue<string>(span, TelegramTelemetry.EventTypeKey).Should().Be("command");
    }

    [Fact]
    public void StartQueueDrainSpan_TagsOutboundMessageId_AndCorrelationId()
    {
        // Iter-3 — correlation-id-scoped listener (see StartCommandSpan
        // test for the parallelism rationale).
        const string correlationId = "trace-q-tagcheck";
        using var listener = new SpanCollector(
            TelegramTelemetry.ActivitySourceName,
            a => string.Equals(
                a.GetTagItem(TelegramTelemetry.CorrelationIdKey) as string,
                correlationId,
                StringComparison.Ordinal));
        var msgId = Guid.NewGuid();

        using (TelegramTelemetry.StartQueueDrainSpan(correlationId, msgId))
        {
        }

        var span = listener.Spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be(TelegramTelemetry.QueueDrainActivityName);
        span.Kind.Should().Be(ActivityKind.Consumer,
            "the queue drain is a Consumer-kind span — the processor is consuming a queued message");
        TagValue<string>(span, TelegramTelemetry.CorrelationIdKey).Should().Be(correlationId);
        TagValue<Guid>(span, "messaging.outbound.message_id").Should().Be(msgId);
    }

    // ============================================================
    // Counter emissions are observable via MeterListener
    // ============================================================

    [Fact]
    public void Counters_AreObservable_OnTheCanonicalMeter()
    {
        using var collector = new CounterCollector(TelegramTelemetry.MeterName);

        TelegramTelemetry.MessagesReceivedCounter.Add(1, new KeyValuePair<string, object?>("event_type", "command"));
        TelegramTelemetry.MessagesSentCounter.Add(2, new KeyValuePair<string, object?>("source_type", "text"));
        TelegramTelemetry.CommandsProcessedCounter.Add(3, new KeyValuePair<string, object?>("command", "status"));
        TelegramTelemetry.ErrorsCounter.Add(4, new KeyValuePair<string, object?>("error_kind", "send_transient"));

        collector.Total("telegram.messages.received").Should().Be(1);
        collector.Total("telegram.messages.sent").Should().Be(2);
        collector.Total("telegram.commands.processed").Should().Be(3);
        collector.Total("telegram.errors").Should().Be(4);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static Instrument? ResolveInstrument(string instrumentName)
    {
        Instrument? found = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, TelegramTelemetry.MeterName, StringComparison.Ordinal)
                && instrument.Name == instrumentName)
            {
                found = instrument;
            }
        };
        listener.Start();
        return found;
    }

    private static T? TagValue<T>(Activity span, string key)
    {
        var raw = span.GetTagItem(key);
        return raw is T typed ? typed : default;
    }

    /// <summary>
    /// xUnit-friendly <see cref="ActivityListener"/> that captures every
    /// span emitted from a named <see cref="ActivitySource"/>, with
    /// <see cref="ActivitySamplingResult.AllDataAndRecorded"/> so the
    /// activity is fully materialized (even in test hosts that have no
    /// TracerProvider configured).
    ///
    /// Iter-3 — the optional <paramref name="filter"/> predicate is the
    /// fix for xUnit's per-class parallelism leaking spans across
    /// concurrent SpanCollector instances. The Stage 6.1 acceptance
    /// test, the queue-depth integration tests, and any sibling
    /// telemetry test all share the single <c>AgentSwarm.Messaging.Telegram</c>
    /// ActivitySource, so every listener subscribed to that source
    /// observes every test's spans. Each test passes a filter pinned
    /// to its unique correlation id (or message-id) so the resulting
    /// <see cref="Spans"/> collection contains ONLY spans this test
    /// itself created — without the filter, <c>ContainSingle</c>
    /// assertions are inherently flaky under parallel execution.
    /// </summary>
    private sealed class SpanCollector : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentBag<Activity> _spans = new();
        private readonly Func<Activity, bool> _filter;

        public SpanCollector(string sourceName, Func<Activity, bool>? filter = null)
        {
            _filter = filter ?? (_ => true);
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _)
                    => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _)
                    => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a =>
                {
                    if (_filter(a))
                    {
                        _spans.Add(a);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyCollection<Activity> Spans => _spans;

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// xUnit-friendly <see cref="MeterListener"/> that captures every
    /// long counter measurement on a named meter and exposes the
    /// per-instrument totals.
    /// </summary>
    private sealed class CounterCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly ConcurrentDictionary<string, long> _totals = new(StringComparer.Ordinal);

        public CounterCollector(string meterName)
        {
            _listener = new MeterListener();
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal)
                    && instrument is Counter<long>)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                _totals.AddOrUpdate(instrument.Name, measurement, (_, acc) => acc + measurement);
            });
            _listener.Start();
        }

        public long Total(string instrumentName) =>
            _totals.TryGetValue(instrumentName, out var v) ? v : 0;

        public void Dispose() => _listener.Dispose();
    }
}
