// -----------------------------------------------------------------------
// <copyright file="SlackOutboundDispatcherTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 6.3 hosted-service tests for the
/// <see cref="SlackOutboundDispatcher"/>. Covers the three brief
/// scenarios literally:
/// <list type="number">
///   <item><description>Message dispatched to thread.</description></item>
///   <item><description>Rate limiter throttles burst.</description></item>
///   <item><description>DLQ on persistent failure.</description></item>
/// </list>
/// </summary>
public sealed class SlackOutboundDispatcherTests
{
    private const string TeamId = "T-DISP";
    private const string ChannelId = "C-DISP";
    private const string ThreadTs = "1700000000.000100";

    [Fact]
    public async Task Scenario_message_dispatched_to_thread()
    {
        // GIVEN an enqueued envelope for task TASK-1 with an existing
        // thread mapping.
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-1"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "1700000050.000010", "{\"ok\":true}");

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit);

        SlackOutboundEnvelope env = new(
            TaskId: "TASK-1",
            CorrelationId: "corr-1",
            MessageType: SlackOutboundOperationKind.PostMessage,
            BlockKitPayload: "{\"blocks\":[]}",
            ThreadTs: ThreadTs);
        await queue.EnqueueAsync(env);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        // WHEN the dispatcher processes the envelope.
        await WaitUntilAsync(() => dispatch.Calls.Count >= 1, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        // THEN chat.postMessage is called with the correct thread_ts
        // and Block Kit payload AND the mapping's resolved
        // team/channel are used.
        dispatch.Calls.Should().HaveCount(1);
        SlackOutboundDispatchRequest call = dispatch.Calls[0];
        call.Operation.Should().Be(SlackOutboundOperationKind.PostMessage);
        call.TeamId.Should().Be(TeamId);
        call.ChannelId.Should().Be(ChannelId);
        call.ThreadTs.Should().Be(ThreadTs);
        call.BlockKitPayload.Should().Be("{\"blocks\":[]}");

        dlq.Entries.Should().BeEmpty();
        audit.Entries.Should().NotBeEmpty();
        audit.Entries.Should().Contain(e =>
            e.Direction == SlackOutboundDispatcher.DirectionOutbound &&
            e.Outcome == SlackOutboundDispatcher.OutcomeSuccess &&
            e.TaskId == "TASK-1" &&
            e.CorrelationId == "corr-1");
    }

    [Fact]
    public async Task Scenario_rate_limiter_throttles_burst()
    {
        // GIVEN 10 envelopes for the same channel.
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-RL"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "1700000050.000010", "{\"ok\":true}");

        // Stand the rate limiter up with a tiny per-channel ceiling so
        // the throttle is observable inside the test budget without
        // sleeping for tens of seconds.
        SlackConnectorOptions opts = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions
            {
                Tier2 = new SlackRateLimitTier
                {
                    RequestsPerMinute = 600, // 10 tokens / second
                    BurstCapacity = 2,
                    Scope = SlackRateLimitScope.Channel,
                },
            },
        };

        SlackOutboundDispatcher dispatcher = BuildDispatcher(
            queue, threads, dispatch, dlq, audit, optionsOverride: opts);

        for (int i = 0; i < 10; i++)
        {
            await queue.EnqueueAsync(new SlackOutboundEnvelope(
                $"TASK-RL-{i}",
                $"corr-{i}",
                SlackOutboundOperationKind.PostMessage,
                "{\"blocks\":[]}",
                ThreadTs));
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Stopwatch sw = Stopwatch.StartNew();
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dispatch.Calls.Count >= 10, TimeSpan.FromSeconds(8));
        sw.Stop();

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        // THEN all 10 messages dispatched, no HTTP 429 errors, AND
        // the total elapsed time reflects rate limiting: 8 calls past
        // the burst at 10/sec = ~0.8 seconds floor.
        dispatch.Calls.Should().HaveCount(10);
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500),
            "messages past the burst capacity MUST be throttled by the rate limiter");
        dlq.Entries.Should().BeEmpty(
            "successful dispatches must never reach the dead-letter queue");
    }

    [Fact]
    public async Task Scenario_dlq_on_persistent_failure()
    {
        // GIVEN an outbound message that fails with HTTP 500 on all 5
        // retry attempts.
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-DLQ"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Transient(500, "http_500", "boom");

        SlackConnectorOptions opts = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };

        SlackOutboundDispatcher dispatcher = BuildDispatcher(
            queue, threads, dispatch, dlq, audit, optionsOverride: opts);

        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            "TASK-DLQ",
            "corr-dlq",
            SlackOutboundOperationKind.PostMessage,
            "{\"blocks\":[]}",
            ThreadTs));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task run = dispatcher.StartAsync(cts.Token);

        // WHEN max retries are exhausted.
        await WaitUntilAsync(() => dlq.Entries.Count >= 1, TimeSpan.FromSeconds(8));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        // THEN the message is moved to the dead-letter queue.
        dispatch.Calls.Count.Should().Be(5,
            "the dispatcher MUST attempt MaxAttempts (5) dispatches before dead-lettering");

        dlq.Entries.Should().HaveCount(1);
        SlackDeadLetterEntry dle = dlq.Entries[0];
        dle.Source.Should().Be(SlackDeadLetterSource.Outbound);
        dle.CorrelationId.Should().Be("corr-dlq");
        dle.AttemptCount.Should().Be(5);
        dle.AsOutbound().TaskId.Should().Be("TASK-DLQ");

        audit.Entries.Should().Contain(e => e.Outcome == SlackOutboundDispatcher.OutcomeDeadLettered);
    }

    [Fact]
    public async Task Pauses_bucket_on_http_429_and_retries_until_success()
    {
        // GIVEN a sequence: first attempt -> 429 with Retry-After 50ms,
        // second attempt -> success.
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-429"));
        ScriptedDispatchClient dispatch = new(new[]
        {
            SlackOutboundDispatchResult.RateLimited(429, TimeSpan.FromMilliseconds(50), "{\"ok\":false,\"error\":\"ratelimited\"}"),
            SlackOutboundDispatchResult.Success(200, "1700000050.000010", "{\"ok\":true}"),
        });

        SlackConnectorOptions opts = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };

        SlackOutboundDispatcher dispatcher = BuildDispatcher(
            queue, threads, dispatch, dlq, audit, optionsOverride: opts);

        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            "TASK-429",
            "corr-429",
            SlackOutboundOperationKind.PostMessage,
            "{\"blocks\":[]}",
            ThreadTs));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dispatch.CallCount >= 2, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        dispatch.CallCount.Should().Be(2,
            "a 429 followed by a success MUST resolve in two dispatch attempts");
        dlq.Entries.Should().BeEmpty();
        audit.Entries.Should().Contain(e => e.Outcome == SlackOutboundDispatcher.OutcomeRateLimited);
        audit.Entries.Should().Contain(e => e.Outcome == SlackOutboundDispatcher.OutcomeSuccess);
    }

    [Fact]
    public async Task Dead_letters_envelope_when_thread_mapping_missing()
    {
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(mapping: null);
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "x", "{\"ok\":true}");

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit);

        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            "TASK-MISSING",
            "corr-missing",
            SlackOutboundOperationKind.PostMessage,
            "{\"blocks\":[]}",
            ThreadTs));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dlq.Entries.Count >= 1, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        dispatch.Calls.Should().BeEmpty(
            "no mapping means no team/channel resolution -- the dispatcher MUST NOT call Slack at all");
        dlq.Entries.Should().HaveCount(1);
        dlq.Entries[0].Reason.Should().Contain("thread_mapping_missing");
    }

    [Fact]
    public async Task Stops_cleanly_on_cancellation()
    {
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-S"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "x", "{\"ok\":true}");

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit);

        using CancellationTokenSource cts = new();
        await dispatcher.StartAsync(cts.Token);
        cts.Cancel();

        Func<Task> act = () => dispatcher.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Update_message_uses_envelope_message_ts_in_request()
    {
        // GIVEN an UpdateMessage envelope that carries an explicit
        // MessageTs (iter-2 typed field instead of payload extraction).
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-UPD"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "ts", "{\"ok\":true}");

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit);

        SlackOutboundEnvelope env = new(
            TaskId: "TASK-UPD",
            CorrelationId: "corr-upd",
            MessageType: SlackOutboundOperationKind.UpdateMessage,
            BlockKitPayload: "{\"blocks\":[]}",
            ThreadTs: ThreadTs)
        {
            MessageTs = "1700000099.000200",
        };
        await queue.EnqueueAsync(env);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dispatch.Calls.Count >= 1, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        // THEN the dispatch client receives the explicit MessageTs (item #1 fix).
        dispatch.Calls.Should().HaveCount(1);
        SlackOutboundDispatchRequest call = dispatch.Calls[0];
        call.Operation.Should().Be(SlackOutboundOperationKind.UpdateMessage);
        call.MessageTs.Should().Be("1700000099.000200",
            "envelope.MessageTs MUST flow through the dispatcher into the request");
        call.ViewId.Should().BeNull();
        dlq.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_message_falls_back_to_payload_ts_when_envelope_field_missing()
    {
        // GIVEN an UpdateMessage envelope whose MessageTs is unset but
        // whose payload still carries `ts` (legacy producer shape).
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-UPD2"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "ts", "{\"ok\":true}");

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit);

        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            TaskId: "TASK-UPD2",
            CorrelationId: "corr-upd2",
            MessageType: SlackOutboundOperationKind.UpdateMessage,
            BlockKitPayload: "{\"ts\":\"1700000050.000010\",\"blocks\":[]}",
            ThreadTs: ThreadTs));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dispatch.Calls.Count >= 1, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        dispatch.Calls.Should().HaveCount(1);
        dispatch.Calls[0].MessageTs.Should().Be("1700000050.000010",
            "legacy payload extraction MUST still work when the typed field is unset");
    }

    [Fact]
    public async Task Views_update_uses_envelope_view_id_in_request()
    {
        // GIVEN a ViewsUpdate envelope that carries an explicit ViewId
        // (iter-2 typed field instead of payload extraction).
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-VIEW"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "ts", "{\"ok\":true}");

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit);

        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            TaskId: "TASK-VIEW",
            CorrelationId: "corr-view",
            MessageType: SlackOutboundOperationKind.ViewsUpdate,
            BlockKitPayload: "{\"view\":{\"blocks\":[]}}",
            ThreadTs: null)
        {
            ViewId = "V123ABC",
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dispatch.Calls.Count >= 1, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        dispatch.Calls.Should().HaveCount(1);
        dispatch.Calls[0].Operation.Should().Be(SlackOutboundOperationKind.ViewsUpdate);
        dispatch.Calls[0].ViewId.Should().Be("V123ABC",
            "envelope.ViewId MUST flow through the dispatcher into the request");
        dlq.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Permanent_failure_records_actual_attempt_count_not_max_attempts()
    {
        // GIVEN an envelope that hits a permanent 4xx on the very first
        // attempt (no retries by design).
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-PERM"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Permanent(403, "missing_scope", "{\"ok\":false}");

        SlackConnectorOptions opts = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit, optionsOverride: opts);

        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            "TASK-PERM",
            "corr-perm",
            SlackOutboundOperationKind.PostMessage,
            "{\"blocks\":[]}",
            ThreadTs));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dlq.Entries.Count >= 1, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        // THEN exactly ONE dispatch attempt was made and DLQ records it
        // (item #5 fix: non-retryable failures must NOT report
        // attemptCount == MaxAttempts).
        dispatch.Calls.Should().HaveCount(1, "permanent failures MUST NOT retry");
        dlq.Entries.Should().HaveCount(1);
        dlq.Entries[0].AttemptCount.Should().Be(1,
            "DLQ AttemptCount MUST reflect the actual number of dispatch attempts, not MaxAttempts");
        dlq.Entries[0].Reason.Should().ContainAny("permanent", "missing_scope");
    }

    [Fact]
    public async Task Missing_configuration_failure_records_single_attempt()
    {
        // GIVEN a ViewsUpdate envelope missing the required ViewId field
        // (and no view_id in the payload), which the dispatch client
        // short-circuits to MissingConfiguration on the very first
        // attempt -- a non-retryable failure.
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-MC"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.MissingConfiguration("view_id_required");

        SlackConnectorOptions opts = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit, optionsOverride: opts);

        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            "TASK-MC",
            "corr-mc",
            SlackOutboundOperationKind.ViewsUpdate,
            "{\"view\":{}}",
            ThreadTs: null));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dlq.Entries.Count >= 1, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        dispatch.Calls.Should().HaveCount(1, "missing-configuration MUST NOT retry");
        dlq.Entries.Should().HaveCount(1);
        dlq.Entries[0].AttemptCount.Should().Be(1,
            "DLQ AttemptCount MUST equal the single dispatch attempt for MissingConfiguration");
    }

    [Fact]
    public async Task Dead_letter_queue_failure_leaves_envelope_unacked_and_writes_failed_audit()
    {
        // GIVEN an envelope that exhausts retries AND whose DLQ enqueue
        // call also fails.
        AcknowledgingChannelQueue queue = new();
        FailingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-DLQF"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Transient(500, "http_500", "boom");

        SlackConnectorOptions opts = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 2, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit, optionsOverride: opts);

        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            "TASK-DLQF",
            "corr-dlqf",
            SlackOutboundOperationKind.PostMessage,
            "{\"blocks\":[]}",
            ThreadTs));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        // WHEN max retries are exhausted AND DLQ enqueue throws.
        await WaitUntilAsync(
            () => audit.Entries.Any(e => e.Outcome == SlackOutboundDispatcher.OutcomeDeadLetterFailed),
            TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        // THEN the envelope MUST NOT be ack'd (durable queue keeps it
        // for replay) AND the audit row MUST be 'dead_letter_failed',
        // NOT 'dead_lettered' (item #4 fix).
        dlq.EnqueueAttempts.Should().BeGreaterThan(0);
        queue.AckedEnvelopes.Should().BeEmpty(
            "DLQ failure means the message is not durable yet; the queue MUST NOT be acked");
        audit.Entries.Should().Contain(e =>
            e.Outcome == SlackOutboundDispatcher.OutcomeDeadLetterFailed &&
            e.TaskId == "TASK-DLQF");
        audit.Entries.Should().NotContain(e =>
            e.Outcome == SlackOutboundDispatcher.OutcomeDeadLettered &&
            e.TaskId == "TASK-DLQF");
    }

    [Fact]
    public async Task Successful_dispatch_acknowledges_durable_queue()
    {
        // GIVEN a durable (acknowledgeable) queue.
        AcknowledgingChannelQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter audit = new();
        StubThreadManager threads = new(BuildMapping("TASK-ACK"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "x", "{\"ok\":true}");

        SlackOutboundDispatcher dispatcher = BuildDispatcher(queue, threads, dispatch, dlq, audit);

        SlackOutboundEnvelope env = new(
            "TASK-ACK",
            "corr-ack",
            SlackOutboundOperationKind.PostMessage,
            "{\"blocks\":[]}",
            ThreadTs);
        await queue.EnqueueAsync(env);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => queue.AckedEnvelopes.Count >= 1, TimeSpan.FromSeconds(5));

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        // THEN the acknowledgeable queue saw exactly one ack for this
        // envelope id.
        queue.AckedEnvelopes.Should().ContainSingle()
            .Which.Should().Be(env.EnvelopeId,
                "successful dispatches MUST be acknowledged so durable queues can delete the journal entry");
    }

    private static SlackOutboundDispatcher BuildDispatcher(
        ISlackOutboundQueue queue,
        ISlackThreadManager threads,
        ISlackOutboundDispatchClient dispatch,
        ISlackDeadLetterQueue dlq,
        ISlackAuditEntryWriter audit,
        SlackConnectorOptions? optionsOverride = null)
    {
        SlackConnectorOptions opts = optionsOverride ?? new SlackConnectorOptions
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };

        IOptionsMonitor<SlackConnectorOptions> monitor =
            new StaticOptionsMonitor<SlackConnectorOptions>(opts);

        SlackTokenBucketRateLimiter limiter = new(monitor, TimeProvider.System);
        DefaultSlackRetryPolicy retry = new(monitor);

        return new SlackOutboundDispatcher(
            queue,
            threads,
            dispatch,
            limiter,
            retry,
            dlq,
            audit,
            monitor,
            NullLogger<SlackOutboundDispatcher>.Instance,
            TimeProvider.System);
    }

    private static SlackThreadMapping BuildMapping(string taskId) => new()
    {
        TaskId = taskId,
        TeamId = TeamId,
        ChannelId = ChannelId,
        ThreadTs = ThreadTs,
        AgentId = "agent-x",
        CorrelationId = "corr-x",
        CreatedAt = DateTimeOffset.UtcNow,
        LastMessageAt = DateTimeOffset.UtcNow,
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException($"predicate did not become true within {timeout}.");
    }

    private static async Task SuppressCancel(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private sealed class StubThreadManager : ISlackThreadManager
    {
        private readonly SlackThreadMapping? mapping;

        public StubThreadManager(SlackThreadMapping? mapping)
        {
            this.mapping = mapping;
        }

        public Task<SlackThreadMapping> GetOrCreateThreadAsync(
            string taskId, string agentId, string correlationId, string teamId, CancellationToken ct)
            => Task.FromResult(this.mapping ?? throw new InvalidOperationException("no mapping configured"));

        public Task<SlackThreadMapping?> GetThreadAsync(string taskId, CancellationToken ct)
            => Task.FromResult(this.mapping);

        public Task<bool> TouchAsync(string taskId, CancellationToken ct)
            => Task.FromResult(this.mapping is not null);

        public Task<SlackThreadMapping?> RecoverThreadAsync(
            string taskId, string agentId, string correlationId, string teamId, CancellationToken ct)
            => Task.FromResult(this.mapping);

        public Task<SlackThreadPostResult> PostThreadedReplyAsync(
            string taskId, string text, string? correlationId, CancellationToken ct)
            => Task.FromResult(this.mapping is not null
                ? SlackThreadPostResult.Posted(this.mapping, "x")
                : SlackThreadPostResult.MappingMissing(taskId));
    }

    private sealed class RecordingDispatchClient : ISlackOutboundDispatchClient
    {
        private readonly List<SlackOutboundDispatchRequest> calls = new();

        public IReadOnlyList<SlackOutboundDispatchRequest> Calls => this.calls;

        public SlackOutboundDispatchResult NextResult { get; set; }
            = SlackOutboundDispatchResult.Success(200, "x", "{\"ok\":true}");

        public Task<SlackOutboundDispatchResult> DispatchAsync(SlackOutboundDispatchRequest request, CancellationToken ct)
        {
            lock (this.calls)
            {
                this.calls.Add(request);
            }

            return Task.FromResult(this.NextResult);
        }
    }

    private sealed class RecordingDeadLetterQueue : ISlackDeadLetterQueue
    {
        private readonly List<SlackDeadLetterEntry> entries = new();

        public IReadOnlyList<SlackDeadLetterEntry> Entries
        {
            get
            {
                lock (this.entries)
                {
                    return this.entries.ToArray();
                }
            }
        }

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ct.ThrowIfCancellationRequested();
            lock (this.entries)
            {
                this.entries.Add(entry);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(this.Entries);
        }
    }

    private sealed class ScriptedDispatchClient : ISlackOutboundDispatchClient
    {
        private readonly Queue<SlackOutboundDispatchResult> scripted;
        private int callCount;

        public ScriptedDispatchClient(IEnumerable<SlackOutboundDispatchResult> results)
        {
            this.scripted = new Queue<SlackOutboundDispatchResult>(results);
        }

        public int CallCount => this.callCount;

        public Task<SlackOutboundDispatchResult> DispatchAsync(SlackOutboundDispatchRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref this.callCount);
            SlackOutboundDispatchResult next = this.scripted.Count > 0
                ? this.scripted.Dequeue()
                : SlackOutboundDispatchResult.Transient(500, "exhausted_script", null);
            return Task.FromResult(next);
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T value;

        public StaticOptionsMonitor(T value)
        {
            this.value = value;
        }

        public T CurrentValue => this.value;

        public T Get(string? name) => this.value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// Test-only acknowledgeable queue: composes the channel-based queue
    /// for in-memory delivery and tracks <see cref="AckedEnvelopes"/> so
    /// we can assert the dispatcher's terminal-disposition ack flow
    /// (item #4 evaluator fix: DLQ failure MUST NOT ack).
    /// </summary>
    private sealed class AcknowledgingChannelQueue : IAcknowledgeableSlackOutboundQueue
    {
        private readonly ChannelBasedSlackOutboundQueue inner = new();
        private readonly List<Guid> acked = new();

        public IReadOnlyList<Guid> AckedEnvelopes
        {
            get
            {
                lock (this.acked)
                {
                    return this.acked.ToArray();
                }
            }
        }

        public ValueTask EnqueueAsync(SlackOutboundEnvelope envelope)
            => this.inner.EnqueueAsync(envelope);

        public ValueTask<SlackOutboundEnvelope> DequeueAsync(CancellationToken ct)
            => this.inner.DequeueAsync(ct);

        public Task AcknowledgeAsync(SlackOutboundEnvelope envelope, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            lock (this.acked)
            {
                this.acked.Add(envelope.EnvelopeId);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Test-only DLQ that always fails to enqueue. Used to exercise the
    /// dispatcher's <c>dead_letter_failed</c> audit + skip-ack flow.
    /// </summary>
    private sealed class FailingDeadLetterQueue : ISlackDeadLetterQueue
    {
        private int enqueueAttempts;

        public int EnqueueAttempts => Volatile.Read(ref this.enqueueAttempts);

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
        {
            Interlocked.Increment(ref this.enqueueAttempts);
            throw new InvalidOperationException("simulated DLQ persistence failure");
        }

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(Array.Empty<SlackDeadLetterEntry>());
    }
}
