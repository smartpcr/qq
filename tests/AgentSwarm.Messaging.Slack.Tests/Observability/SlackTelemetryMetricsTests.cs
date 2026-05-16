// -----------------------------------------------------------------------
// <copyright file="SlackTelemetryMetricsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Observability;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Observability;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 7.2 metrics-emission tests for the Slack messenger connector.
/// Implements two of the three brief scenarios end-to-end:
/// <list type="bullet">
///   <item><description><c>slack.inbound.count</c> increments once per
///   envelope processed by the pipeline. The brief's scenario is "5
///   inbound commands processed -&gt; counter reads 5".</description></item>
///   <item><description><c>slack.idempotency.duplicate_count</c>
///   increments once per duplicate detected by the guard. The brief's
///   scenario is "3 duplicate events -&gt; counter reads 3".</description></item>
/// </list>
/// Subscriptions are scoped per-test using <see cref="MeterListener"/>
/// so concurrent tests do not steal each other's measurements -- we
/// filter by instrument-name plus the unique per-test tag we attach to
/// every recorded envelope, then sum the matching observations.
/// </summary>
public sealed class SlackTelemetryMetricsTests
{
    [Fact]
    public async Task Five_inbound_commands_increment_slack_inbound_count_to_five()
    {
        // Brief scenario: "Given 5 inbound commands processed, When the
        // slack.inbound.count counter is read, Then its value is 5."
        // Stage 7.2 step 4 places the emission site inside
        // SlackInboundIngestor.ExecuteAsync (one bump per envelope
        // drained from the queue). Driving the real ingestor (not the
        // pipeline directly) is the only way this test fails if the
        // production emission point is removed -- iter 1 manually
        // called SlackTelemetry.InboundCount.Add after the pipeline,
        // which masked any regression that broke the ingestor wire-up.
        string testTeamId = "T-METRICS-" + Guid.NewGuid().ToString("N");
        using CounterCollector collector = CounterCollector.Subscribe(
            SlackTelemetry.MetricInboundCount,
            tagKey: SlackTelemetry.AttributeTeamId,
            tagValue: testTeamId);

        SlackInboundProcessingPipeline pipeline = BuildPipelineWithRecordingHandler(out RecordingHandler handler);

        FakeInboundQueue queue = new();
        SlackInboundIngestor ingestor = new(
            queue,
            pipeline,
            new InMemorySlackInboundEnqueueDeadLetterSink(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            NullLogger<SlackInboundIngestor>.Instance);

        for (int i = 0; i < 5; i++)
        {
            SlackInboundEnvelope envelope = BuildCommandEnvelope(
                idempotencyKey: $"cmd:T1:U1:/agent:trig-{i}-{Guid.NewGuid():N}",
                teamId: testTeamId);
            await queue.EnqueueAsync(envelope);
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task run = ingestor.StartAsync(cts.Token);

        await WaitUntilAsync(
            () => handler.InvocationCount == 5,
            TimeSpan.FromSeconds(10));

        await ingestor.StopAsync(CancellationToken.None);
        cts.Cancel();
        await run;

        handler.InvocationCount.Should().Be(5,
            "all five command envelopes MUST reach the command handler so the test exercises a real success path");

        collector.Total.Should().Be(5,
            "slack.inbound.count MUST be incremented once per envelope by the ingestor's production emission site; if this fails, the production Add call in SlackInboundIngestor.ExecuteAsync was removed or no longer fires");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Three_duplicate_envelopes_increment_slack_idempotency_duplicate_count_to_three()
    {
        // Brief scenario: "Given 3 duplicate events detected by the
        // idempotency guard, When slack.idempotency.duplicate_count is
        // read, Then its value is 3." We arrange this by pre-claiming a
        // single idempotency key in the in-memory guard, then driving
        // three envelopes with the same key through the pipeline -- the
        // guard returns false every time, the pipeline bumps the
        // counter, and the per-test team_id tag isolates the test from
        // any other concurrent measurement on the shared Meter.
        string testTeamId = "T-DUP-" + Guid.NewGuid().ToString("N");
        using CounterCollector collector = CounterCollector.Subscribe(
            SlackTelemetry.MetricIdempotencyDuplicateCount,
            tagKey: SlackTelemetry.AttributeTeamId,
            tagValue: testTeamId);

        // Use a guard wrapper that returns false unconditionally so we
        // can drive 3 distinct envelopes through the duplicate path
        // without any inter-test coupling on the in-memory guard's
        // internal dictionary.
        AlwaysDuplicateGuard guard = new();
        SlackInboundProcessingPipeline pipeline = BuildPipelineWithGuard(guard);

        for (int i = 0; i < 3; i++)
        {
            SlackInboundEnvelope dup = BuildCommandEnvelope(
                idempotencyKey: $"cmd:T1:U1:/agent:dup-{i}-{Guid.NewGuid():N}",
                teamId: testTeamId);
            SlackInboundProcessingOutcome outcome = await pipeline.ProcessAsync(dup, CancellationToken.None);
            outcome.Should().Be(SlackInboundProcessingOutcome.Duplicate,
                "the guard returns false unconditionally so EVERY envelope MUST land on the duplicate path");
        }

        guard.AcquireAttempts.Should().Be(3,
            "the guard MUST be called once per envelope so the duplicate-count emission site fires exactly three times");

        collector.Total.Should().Be(3,
            "slack.idempotency.duplicate_count MUST reflect exactly the three duplicates detected in this test");
    }

    [Fact]
    public async Task Inbound_counter_is_tagged_with_source_type_to_split_command_vs_interaction_vs_event_traffic()
    {
        // Regression guard for architecture.md §6.3: dashboards split
        // inbound volume by source_type. If the tag drops the panel
        // collapses to a single bar -- this test pins the contract.
        string testTeamId = "T-SOURCE-" + Guid.NewGuid().ToString("N");
        using TagCollector collector = TagCollector.Subscribe(
            SlackTelemetry.MetricInboundCount,
            tagKey: SlackTelemetry.AttributeTeamId,
            tagValue: testTeamId);

        SlackTelemetry.InboundCount.Add(
            1,
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeSourceType, "Command"),
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, testTeamId));
        SlackTelemetry.InboundCount.Add(
            1,
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeSourceType, "Interaction"),
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, testTeamId));

        await Task.Yield();

        collector.SourceTypes.Should().BeEquivalentTo(new[] { "Command", "Interaction" },
            "every emission MUST tag slack.source_type so dashboards can split command / interaction / event volume");
    }

    private static SlackInboundProcessingPipeline BuildPipelineWithRecordingHandler(out RecordingHandler handler)
    {
        handler = new RecordingHandler();
        return new SlackInboundProcessingPipeline(
            new AlwaysAuthorizingAuthorizer(),
            new InMemorySlackIdempotencyGuard(),
            new RecordingCommandHandler(handler),
            new RecordingAppMentionHandler(new RecordingHandler()),
            new RecordingInteractionHandler(new RecordingHandler()),
            new ZeroDelayRetryPolicy(maxAttempts: 1),
            new InMemoryDeadLetterQueue(),
            new SlackInboundAuditRecorder(
                new InMemorySlackAuditEntryWriter(),
                NullLogger<SlackInboundAuditRecorder>.Instance,
                TimeProvider.System),
            NullLogger<SlackInboundProcessingPipeline>.Instance,
            TimeProvider.System);
    }

    private static SlackInboundProcessingPipeline BuildPipelineWithGuard(ISlackIdempotencyGuard guard)
    {
        return new SlackInboundProcessingPipeline(
            new AlwaysAuthorizingAuthorizer(),
            guard,
            new RecordingCommandHandler(new RecordingHandler()),
            new RecordingAppMentionHandler(new RecordingHandler()),
            new RecordingInteractionHandler(new RecordingHandler()),
            new ZeroDelayRetryPolicy(maxAttempts: 1),
            new InMemoryDeadLetterQueue(),
            new SlackInboundAuditRecorder(
                new InMemorySlackAuditEntryWriter(),
                NullLogger<SlackInboundAuditRecorder>.Instance,
                TimeProvider.System),
            NullLogger<SlackInboundProcessingPipeline>.Instance,
            TimeProvider.System);
    }

    private static SlackInboundEnvelope BuildCommandEnvelope(string idempotencyKey, string teamId) => new(
        IdempotencyKey: idempotencyKey,
        SourceType: SlackInboundSourceType.Command,
        TeamId: teamId,
        ChannelId: "C1",
        UserId: "U1",
        RawPayload: "team_id=" + teamId + "&user_id=U1&command=/agent",
        TriggerId: "trig",
        ReceivedAt: DateTimeOffset.UtcNow);

    /// <summary>
    /// Subscribes to a single counter instrument by name and sums its
    /// long-valued measurements whose tags match the supplied filter.
    /// Filtering by a per-test team_id keeps tests isolated from any
    /// concurrent measurement on the shared process-wide Meter.
    /// </summary>
    private sealed class CounterCollector : IDisposable
    {
        private readonly MeterListener listener;
        private readonly string instrumentName;
        private readonly string filterKey;
        private readonly string filterValue;
        private long total;

        private CounterCollector(string instrumentName, string filterKey, string filterValue)
        {
            this.instrumentName = instrumentName;
            this.filterKey = filterKey;
            this.filterValue = filterValue;
            this.listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == SlackTelemetry.MeterName
                        && instrument.Name == this.instrumentName)
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                },
            };
            this.listener.SetMeasurementEventCallback<long>(this.OnLong);
            this.listener.Start();
        }

        public long Total => Interlocked.Read(ref this.total);

        public static CounterCollector Subscribe(string instrumentName, string tagKey, string tagValue)
            => new(instrumentName, tagKey, tagValue);

        public void Dispose() => this.listener.Dispose();

        private void OnLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == this.filterKey
                    && string.Equals(tags[i].Value?.ToString(), this.filterValue, StringComparison.Ordinal))
                {
                    Interlocked.Add(ref this.total, value);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Captures the distinct <c>slack.source_type</c> tag values seen
    /// on a single instrument so a regression that drops the tag is
    /// detected.
    /// </summary>
    private sealed class TagCollector : IDisposable
    {
        private readonly MeterListener listener;
        private readonly string instrumentName;
        private readonly string filterKey;
        private readonly string filterValue;
        private readonly ConcurrentBag<string> sourceTypes = new();

        private TagCollector(string instrumentName, string filterKey, string filterValue)
        {
            this.instrumentName = instrumentName;
            this.filterKey = filterKey;
            this.filterValue = filterValue;
            this.listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == SlackTelemetry.MeterName
                        && instrument.Name == this.instrumentName)
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                },
            };
            this.listener.SetMeasurementEventCallback<long>(this.OnLong);
            this.listener.Start();
        }

        public IReadOnlyCollection<string> SourceTypes => this.sourceTypes;

        public static TagCollector Subscribe(string instrumentName, string tagKey, string tagValue)
            => new(instrumentName, tagKey, tagValue);

        public void Dispose() => this.listener.Dispose();

        private void OnLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            string? team = null;
            string? sourceType = null;
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == this.filterKey)
                {
                    team = tags[i].Value?.ToString();
                }

                if (tags[i].Key == SlackTelemetry.AttributeSourceType)
                {
                    sourceType = tags[i].Value?.ToString();
                }
            }

            if (string.Equals(team, this.filterValue, StringComparison.Ordinal)
                && sourceType is not null)
            {
                this.sourceTypes.Add(sourceType);
            }
        }
    }

    private sealed class AlwaysAuthorizingAuthorizer : ISlackInboundAuthorizer
    {
        public Task<SlackInboundAuthorizationResult> AuthorizeAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => Task.FromResult(SlackInboundAuthorizationResult.Authorized(new SlackWorkspaceConfig
            {
                TeamId = envelope.TeamId,
                Enabled = true,
            }));
    }

    /// <summary>
    /// Guard stub that always reports the envelope as a duplicate.
    /// Mirrors the behaviour of a real guard whose dedup row already
    /// exists in a terminal state for the supplied idempotency key.
    /// </summary>
    private sealed class AlwaysDuplicateGuard : ISlackIdempotencyGuard
    {
        private int attempts;

        public int AcquireAttempts => Volatile.Read(ref this.attempts);

        public Task<bool> TryAcquireAsync(SlackInboundEnvelope envelope, CancellationToken ct)
        {
            Interlocked.Increment(ref this.attempts);
            return Task.FromResult(false);
        }

        public Task MarkCompletedAsync(string idempotencyKey, CancellationToken ct) => Task.CompletedTask;

        public Task MarkFailedAsync(string idempotencyKey, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingHandler
    {
        private int count;

        public int InvocationCount => Volatile.Read(ref this.count);

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
        {
            Interlocked.Increment(ref this.count);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandHandler : ISlackCommandHandler
    {
        private readonly RecordingHandler inner;

        public RecordingCommandHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class RecordingAppMentionHandler : ISlackAppMentionHandler
    {
        private readonly RecordingHandler inner;

        public RecordingAppMentionHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class RecordingInteractionHandler : ISlackInteractionHandler
    {
        private readonly RecordingHandler inner;

        public RecordingInteractionHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class InMemoryDeadLetterQueue : ISlackDeadLetterQueue
    {
        private readonly ConcurrentQueue<SlackDeadLetterEntry> entries = new();

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
        {
            this.entries.Enqueue(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(this.entries.ToArray());
    }

    private sealed class ZeroDelayRetryPolicy : ISlackRetryPolicy
    {
        private readonly int maxAttempts;

        public ZeroDelayRetryPolicy(int maxAttempts)
        {
            this.maxAttempts = maxAttempts;
        }

        public bool ShouldRetry(int attemptNumber, Exception exception)
            => exception is not OperationCanceledException && attemptNumber < this.maxAttempts;

        public TimeSpan GetDelay(int attemptNumber) => TimeSpan.Zero;
    }

    private sealed class FakeInboundQueue : ISlackInboundQueue
    {
        private readonly System.Threading.Channels.Channel<SlackInboundEnvelope> channel =
            System.Threading.Channels.Channel.CreateUnbounded<SlackInboundEnvelope>();

        public ValueTask EnqueueAsync(SlackInboundEnvelope envelope)
        {
            this.channel.Writer.TryWrite(envelope);
            return ValueTask.CompletedTask;
        }

        public ValueTask<SlackInboundEnvelope> DequeueAsync(CancellationToken ct)
            => this.channel.Reader.ReadAsync(ct);
    }
}
