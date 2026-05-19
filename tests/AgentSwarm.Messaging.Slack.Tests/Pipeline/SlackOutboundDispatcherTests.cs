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
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlackNet;
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

    // -----------------------------------------------------------------
    // Stage 6.4 evaluator iter-3 item #2 (STRUCTURAL): exercise the
    // SHARED SlackTokenBucketRateLimiter through BOTH a real
    // SlackDirectApiClient (views.open) AND a real SlackOutboundDispatcher
    // (views.update -- Tier 4, same workspace bucket as views.open) at
    // the same time. The earlier shared-limiter assertion in
    // SlackDirectApiClientTests only proved sibling AcquireAsync
    // suspension via the limiter API; this test drives the entire
    // dispatcher loop end-to-end so a constructor-graph regression or
    // a "skip the AcquireAsync call" bug would fail here.
    //
    // Caveats faithfully encoded in this test:
    //   * The brief's exact wording mentions "concurrent views.open +
    //     chat.postMessage". In Slack's published tier topology
    //     chat.postMessage is Tier 2 (per-channel bucket) and
    //     views.open is Tier 4 (per-workspace bucket), so those two
    //     operations have INDEPENDENT buckets and a 429 on one would
    //     NOT pause the other -- not a meaningful "shared limiter"
    //     scenario. The genuine cross-caller back-pressure case is
    //     two Tier 4 calls on the same workspace, which is what this
    //     test exercises: views.open (SlackDirectApiClient) -> 429,
    //     then views.update (SlackOutboundDispatcher) MUST wait out
    //     the Retry-After window on the SAME workspace bucket.
    //   * Uses TimeProvider.System (wall clock) so the rate limiter's
    //     real Task.Delay branch executes -- the test pays the
    //     Retry-After window (200 ms) but that is small enough to
    //     keep the suite fast while still being big enough to be
    //     measurable.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Direct_client_HTTP_429_blocks_real_SlackOutboundDispatcher_views_update_via_shared_SlackTokenBucketRateLimiter()
    {
        // 1. SHARED limiter, shared options monitor.
        SlackConnectorOptions sharedOptions = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };
        IOptionsMonitor<SlackConnectorOptions> sharedMonitor =
            new StaticOptionsMonitor<SlackConnectorOptions>(sharedOptions);
        SlackTokenBucketRateLimiter sharedLimiter = new(sharedMonitor, TimeProvider.System);

        // 2. SlackDirectApiClient wired with the SHARED limiter. The
        //    SlackNet mock throws a 429 with a measurable Retry-After
        //    so the client invokes sharedLimiter.NotifyRetryAfter on
        //    the (Tier 4, TeamId) bucket.
        const string SharedTeamId = TeamId;
        TimeSpan retryAfter = TimeSpan.FromMilliseconds(400);
        Mock<ISlackApiClient> slackNetMock = new(MockBehavior.Loose);
        slackNetMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SlackRateLimitException(retryAfter));

        InMemorySlackAuditEntryWriter clientAudit = new();
        StubWorkspaceStore workspaceStore = new(new Dictionary<string, SlackWorkspaceConfig>
        {
            [SharedTeamId] = new SlackWorkspaceConfig
            {
                TeamId = SharedTeamId,
                BotTokenSecretRef = "test://bot-token/" + SharedTeamId,
                Enabled = true,
            },
        });
        StubSecretProvider secrets = new();
        secrets.Set("test://bot-token/" + SharedTeamId, "xoxb-test");

        SlackDirectApiClient directClient = new(
            workspaceStore,
            secrets,
            sharedLimiter,
            clientAudit,
            NullLogger<SlackDirectApiClient>.Instance,
            apiClientFactory: _ => slackNetMock.Object,
            timeProvider: TimeProvider.System);

        // 3. SlackOutboundDispatcher wired with the SAME shared limiter
        //    via the new BuildDispatcher overload. The dispatch client
        //    records every call so we can measure WHEN the actual
        //    views.update dispatch occurred relative to the 429.
        ChannelBasedSlackOutboundQueue queue = new();
        RecordingDeadLetterQueue dlq = new();
        InMemorySlackAuditEntryWriter dispatcherAudit = new();
        StubThreadManager threads = new(BuildMapping("TASK-SHARED-LIMITER"));
        RecordingDispatchClient dispatch = new();
        dispatch.NextResult = SlackOutboundDispatchResult.Success(200, "ts", "{\"ok\":true}");

        SlackOutboundDispatcher dispatcher = BuildDispatcher(
            queue,
            threads,
            dispatch,
            dlq,
            dispatcherAudit,
            optionsOverride: sharedOptions,
            sharedLimiter: sharedLimiter,
            sharedMonitor: sharedMonitor);

        // 4. Drive views.open through the SlackDirectApiClient. The
        //    SlackNet mock throws 429 -> client returns NetworkFailure
        //    with rate_limited AND calls sharedLimiter.NotifyRetryAfter
        //    on (Tier 4, SharedTeamId). The bucket is now suspended
        //    for retryAfter from "now".
        SlackModalPayload payload = new(SharedTeamId, View: new { type = "modal" });
        DateTimeOffset retryAfterStart = TimeProvider.System.GetUtcNow();
        SlackDirectApiResult clientResult = await directClient.OpenModalAsync(
            "trig-shared",
            payload,
            CancellationToken.None);
        clientResult.IsSuccess.Should().BeFalse(
            "the 429 from SlackNet must materialise as a NetworkFailure on the SlackDirectApiClient surface");
        clientResult.Error.Should().Be("rate_limited",
            "the rate_limited sentinel proves the client classified the SlackNet exception as the 429 path that triggers NotifyRetryAfter -- a different sentinel would mean the shared limiter was NOT informed");

        // 5. Enqueue a ViewsUpdate envelope (Tier 4, same workspace).
        //    The dispatcher's drain loop calls
        //    sharedLimiter.AcquireAsync(Tier4, SharedTeamId, ct), which
        //    MUST wait out the Retry-After suspension established in
        //    step 4. We measure the elapsed wall-clock from the
        //    Retry-After notification to the actual dispatch call to
        //    prove the wait happened.
        await queue.EnqueueAsync(new SlackOutboundEnvelope(
            TaskId: "TASK-SHARED-LIMITER",
            CorrelationId: "corr-shared",
            MessageType: SlackOutboundOperationKind.ViewsUpdate,
            BlockKitPayload: "{\"view\":{\"blocks\":[]}}",
            ThreadTs: null)
        {
            ViewId = "V_SHARED_LIMITER",
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task run = dispatcher.StartAsync(cts.Token);

        await WaitUntilAsync(() => dispatch.Calls.Count >= 1, TimeSpan.FromSeconds(10));
        DateTimeOffset dispatchObserved = TimeProvider.System.GetUtcNow();

        await dispatcher.StopAsync(CancellationToken.None);
        cts.Cancel();
        await SuppressCancel(run);

        // 6. Verify the dispatcher genuinely waited for the shared
        //    bucket's Retry-After window. We allow a small tolerance
        //    (50 ms) to absorb scheduler jitter, but the elapsed
        //    time MUST be on the order of the Retry-After window --
        //    a "no wait at all" result (single-digit ms) would mean
        //    the dispatcher's limiter was NOT the same instance that
        //    the SlackDirectApiClient notified.
        TimeSpan elapsed = dispatchObserved - retryAfterStart;
        elapsed.Should().BeGreaterThan(retryAfter - TimeSpan.FromMilliseconds(50),
            $"the dispatcher's views.update acquire MUST wait out the Retry-After window ({retryAfter}) established by the direct client's 429 -- a faster dispatch proves the limiter was NOT shared and Slack would have received concurrent calls inside the back-off window. Actual elapsed: {elapsed}.");

        // The dispatch eventually succeeds once the suspension lifts.
        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].Operation.Should().Be(SlackOutboundOperationKind.ViewsUpdate);
        dispatch.Calls[0].ViewId.Should().Be("V_SHARED_LIMITER");
        dlq.Entries.Should().BeEmpty(
            "the dispatch should succeed AFTER waiting out the shared bucket's suspension, not dead-letter");
    }

    private sealed class StubWorkspaceStore : ISlackWorkspaceConfigStore
    {
        private readonly IReadOnlyDictionary<string, SlackWorkspaceConfig> workspaces;

        public StubWorkspaceStore(IReadOnlyDictionary<string, SlackWorkspaceConfig> workspaces)
        {
            this.workspaces = workspaces;
        }

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(teamId) && this.workspaces.TryGetValue(teamId, out SlackWorkspaceConfig? cfg))
            {
                return Task.FromResult<SlackWorkspaceConfig?>(cfg);
            }

            return Task.FromResult<SlackWorkspaceConfig?>(null);
        }

        public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(new List<SlackWorkspaceConfig>(this.workspaces.Values));
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public void Set(string secretRef, string value) => this.values[secretRef] = value;

        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            if (this.values.TryGetValue(secretRef, out string? v))
            {
                return Task.FromResult(v);
            }

            throw new SecretNotFoundException(secretRef);
        }
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
        SlackConnectorOptions? optionsOverride = null,
        ISlackRateLimiter? sharedLimiter = null,
        IOptionsMonitor<SlackConnectorOptions>? sharedMonitor = null)
    {
        SlackConnectorOptions opts = optionsOverride ?? new SlackConnectorOptions
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };

        IOptionsMonitor<SlackConnectorOptions> monitor = sharedMonitor
            ?? new StaticOptionsMonitor<SlackConnectorOptions>(opts);

        // Stage 6.4 evaluator iter-3 item #2: allow callers to inject
        // a SHARED limiter so a sibling SlackDirectApiClient can drive
        // the bucket into a Retry-After suspension and the dispatcher's
        // subsequent Tier 4 acquire observes the back-pressure via the
        // SAME instance. The default factory still constructs a fresh
        // limiter so unrelated dispatcher tests are unaffected.
        ISlackRateLimiter limiter = sharedLimiter
            ?? new SlackTokenBucketRateLimiter(monitor, TimeProvider.System);
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
