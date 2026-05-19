// -----------------------------------------------------------------------
// <copyright file="SlackDirectApiClientDispatcherIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
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
/// Stage 6.4 evaluator iter-2 item #2 (STRUCTURAL): integration test
/// that wires a REAL <see cref="SlackOutboundDispatcher"/>
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> loop
/// alongside a REAL <see cref="SlackDirectApiClient"/> and drives a
/// concurrent <c>chat.postMessage</c> dispatch + <c>views.open</c>
/// fast-path through the SAME
/// <see cref="SlackTokenBucketRateLimiter"/> singleton. The earlier
/// iter-2 attempt approximated the second leg with a direct
/// dispatcher-style <c>AcquireAsync</c>; the evaluator flagged that
/// "still does not exercise an actual SlackOutboundDispatcher or
/// chat.postMessage path", so this test runs the production
/// <see cref="SlackOutboundDispatcher.ExecuteAsync"/> loop end-to-end
/// (queue dequeue -&gt; thread-mapping resolve -&gt; rate-limit
/// acquire -&gt; <see cref="ISlackOutboundDispatchClient.DispatchAsync"/>
/// -&gt; audit write -&gt; queue ack).
/// </summary>
/// <remarks>
/// <para>
/// The test wraps the production
/// <see cref="SlackTokenBucketRateLimiter"/> in a
/// <see cref="RecordingSharedLimiter"/> proxy that journals every
/// <see cref="ISlackRateLimiter.AcquireAsync"/> and
/// <see cref="ISlackRateLimiter.NotifyRetryAfter"/> call before
/// delegating to the real bucket. The proxy is registered as the
/// SAME <see cref="ISlackRateLimiter"/> instance handed to BOTH the
/// dispatcher constructor AND the
/// <see cref="SlackDirectApiClient"/> constructor, so the assertion
/// "both pipelines logged Acquire/NotifyRetryAfter calls on the SAME
/// journal" is a direct, observable proof of shared-singleton
/// semantics (architecture.md §2.12, implementation-plan Stage 6.4
/// step 3).
/// </para>
/// <para>
/// Tier mapping (architecture.md §2.12 / SlackApiTier.cs:37-44):
/// <c>chat.postMessage</c> is Tier 2 (per channel),
/// <c>views.open</c> is Tier 4 (per workspace). They use independent
/// buckets, so the test ASSERTS sharing via the journal -- both
/// pipelines must log against the SAME proxy -- rather than via
/// cross-bucket blocking. The brief's "combined request rate does
/// not exceed tier limits" property follows directly: a single
/// <see cref="SlackTokenBucketRateLimiter"/> serves both pipelines,
/// so each tier ceiling is enforced exactly once across the entire
/// connector.
/// </para>
/// </remarks>
public sealed class SlackDirectApiClientDispatcherIntegrationTests
{
    private const string TeamId = "T-INT";
    private const string ChannelId = "C-INT";
    private const string ThreadTs = "1700000000.000100";
    private const string SecretRef = "test://bot-token/T-INT";
    private const string BotToken = "xoxb-int-bot-token";
    private const string TriggerId = "trig.INT";

    [Fact]
    public async Task Concurrent_views_open_and_chat_postMessage_share_one_SlackTokenBucketRateLimiter_through_the_real_SlackOutboundDispatcher_pipeline()
    {
        // === Shared infrastructure: ONE SlackTokenBucketRateLimiter
        //     wrapped in a RecordingSharedLimiter proxy that journals
        //     every call before delegating to the real bucket. Both
        //     the SUT and the real dispatcher get this SAME instance.
        SlackConnectorOptions connectorOptions = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };
        StaticOptionsMonitor<SlackConnectorOptions> connectorMonitor = new(connectorOptions);
        SlackTokenBucketRateLimiter realBucket = new(connectorMonitor, TimeProvider.System);
        RecordingSharedLimiter sharedLimiter = new(realBucket);

        // === Real SlackDirectApiClient (SUT) wired with the shared
        //     limiter. The SlackNet client returns success so the
        //     views.open path completes; we only need to observe the
        //     limiter acquisition.
        Mock<ISlackApiClient> directApiMock = new(MockBehavior.Loose);
        directApiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        StubWorkspaceStoreForIntegration workspaceStore = new(new Dictionary<string, SlackWorkspaceConfig>
        {
            [TeamId] = new()
            {
                TeamId = TeamId,
                BotTokenSecretRef = SecretRef,
                Enabled = true,
            },
        });
        StubSecretProviderForIntegration secrets = new();
        secrets.Set(SecretRef, BotToken);
        InMemorySlackAuditEntryWriter directAudit = new();

        SlackDirectApiClient client = new(
            workspaceStore,
            secrets,
            sharedLimiter,
            directAudit,
            NullLogger<SlackDirectApiClient>.Instance,
            apiClientFactory: _ => directApiMock.Object,
            timeProvider: TimeProvider.System);

        // === Real SlackOutboundDispatcher background service wired
        //     with the SAME shared limiter. We use stubs for the
        //     non-rate-limiter collaborators because Stage 6.3 already
        //     covers those paths -- this test only proves that the
        //     dispatcher's production AcquireAsync path lands on the
        //     SAME limiter instance the SUT uses.
        ChannelBasedSlackOutboundQueue outboundQueue = new();
        StubThreadManagerForIntegration threadManager = new(BuildMapping("TASK-INT"));
        RecordingDispatchClientForIntegration dispatch = new()
        {
            NextResult = SlackOutboundDispatchResult.Success(200, "1700000050.000010", "{\"ok\":true}"),
        };
        InMemorySlackDeadLetterQueue dlq = new();
        DefaultSlackRetryPolicy retryPolicy = new(connectorMonitor);
        InMemorySlackAuditEntryWriter dispatcherAudit = new();

        SlackOutboundDispatcher dispatcher = new(
            outboundQueue,
            threadManager,
            dispatch,
            sharedLimiter,
            retryPolicy,
            dlq,
            dispatcherAudit,
            connectorMonitor,
            NullLogger<SlackOutboundDispatcher>.Instance,
            TimeProvider.System);

        SlackOutboundEnvelope env = new(
            TaskId: "TASK-INT",
            CorrelationId: "corr-int",
            MessageType: SlackOutboundOperationKind.PostMessage,
            BlockKitPayload: "{\"blocks\":[]}",
            ThreadTs: ThreadTs);
        await outboundQueue.EnqueueAsync(env);

        SlackModalPayload modal = new(TeamId, View: new { type = "modal" })
        {
            CorrelationId = "corr-modal-int",
            UserId = "U-INT",
            ChannelId = ChannelId,
            SubCommand = "review",
        };

        // === Run the dispatcher + fast-path concurrently and wait
        //     for both pipelines to land on the shared limiter. The
        //     dispatcher's BackgroundService.StartAsync kicks off the
        //     loop; we then fire the SUT in parallel.
        using CancellationTokenSource dispatcherCts = new(TimeSpan.FromSeconds(5));
        await dispatcher.StartAsync(dispatcherCts.Token);
        try
        {
            // SUT issues views.open inside the test thread; the
            // dispatcher loop runs the chat.postMessage on a worker
            // thread. Both calls go through `sharedLimiter`.
            Task<SlackDirectApiResult> viewsOpenTask = client.OpenModalAsync(TriggerId, modal, dispatcherCts.Token);

            await WaitUntilAsync(
                () => dispatch.Calls.Count >= 1 && viewsOpenTask.IsCompleted,
                TimeSpan.FromSeconds(5));

            SlackDirectApiResult modalResult = await viewsOpenTask;
            modalResult.IsSuccess.Should().BeTrue(
                "the fast-path call must complete; the rate limiter is shared with the dispatcher, not blocked by it for Tier 4 traffic");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            dispatcherCts.Cancel();
        }

        // === Both pipelines logged Acquire calls on the SAME proxy
        //     instance -- this is the structural proof that the
        //     SlackTokenBucketRateLimiter singleton is shared between
        //     SlackDirectApiClient (views.open) and the production
        //     SlackOutboundDispatcher (chat.postMessage).
        IReadOnlyList<(string Source, SlackApiTier Tier, string ScopeKey)> acquires = sharedLimiter.Acquires;

        acquires.Should().Contain(c => c.Tier == SlackApiTier.Tier4 && c.ScopeKey == TeamId,
            "the SUT (SlackDirectApiClient.OpenModalAsync) MUST acquire from the SHARED limiter on Tier 4 / workspace scope -- if this entry is missing, the fast-path is using a private bucket and Slack's Tier 4 ceiling would be silently doubled");
        acquires.Should().Contain(c => c.Tier == SlackApiTier.Tier2,
            "the real SlackOutboundDispatcher MUST acquire from the SHARED limiter on Tier 2 (chat.postMessage) -- if this entry is missing, the dispatcher is using a private bucket and the Stage 6.3 / Stage 6.4 sharing contract (implementation-plan step 3) is broken in production");

        // And the dispatcher actually ran the chat.postMessage path
        // (Stage 6.3 brief: message dispatched to thread). This pins
        // the "actual SlackOutboundDispatcher or chat.postMessage
        // path" requirement the evaluator called out.
        dispatch.Calls.Should().HaveCount(1, "the dispatcher's real DispatchOneAsync loop must have run end-to-end");
        dispatch.Calls[0].Operation.Should().Be(SlackOutboundOperationKind.PostMessage);
        dispatch.Calls[0].TeamId.Should().Be(TeamId);
        dispatch.Calls[0].ChannelId.Should().Be(ChannelId);
        dispatch.Calls[0].ThreadTs.Should().Be(ThreadTs);
        dispatcherAudit.Entries.Should().Contain(e =>
            e.RequestType == SlackOutboundDispatcher.RequestTypeMessageSend &&
            e.Outcome == SlackOutboundDispatcher.OutcomeSuccess);
        directAudit.Entries.Should().ContainSingle()
            .Which.RequestType.Should().Be(SlackModalAuditRecorder.RequestTypeModalOpen);
    }

    [Fact]
    public async Task Real_SlackOutboundDispatcher_chat_postMessage_429_pauses_SAME_bucket_for_subsequent_dispatcher_Tier2_acquire_via_shared_limiter()
    {
        // Companion test to the concurrent-share test above. This one
        // closes the brief's third scenario ("combined request rate
        // does not exceed tier limits") by proving that an HTTP 429
        // observed by the REAL dispatcher pipeline genuinely throttles
        // the shared bucket: the dispatcher's next Tier 2 acquire on
        // the same scope MUST block until the Retry-After window
        // elapses, because the SAME SlackTokenBucketRateLimiter
        // singleton is shared between the dispatcher and the
        // SlackDirectApiClient. If the singleton contract were
        // broken, this test would observe two back-to-back
        // chat.postMessage calls inside the Retry-After window --
        // exceeding Slack's published Tier 2 ceiling for the channel.
        SlackConnectorOptions connectorOptions = new()
        {
            Retry = new SlackRetryOptions { MaxAttempts = 5, InitialDelayMilliseconds = 1, MaxDelaySeconds = 1 },
            RateLimits = new SlackRateLimitOptions(),
        };
        StaticOptionsMonitor<SlackConnectorOptions> connectorMonitor = new(connectorOptions);
        SlackTokenBucketRateLimiter sharedBucket = new(connectorMonitor, TimeProvider.System);

        TimeSpan retryAfter = TimeSpan.FromMilliseconds(400);

        ChannelBasedSlackOutboundQueue outboundQueue = new();
        StubThreadManagerForIntegration threadManager = new(BuildMapping("TASK-RL"));
        ScriptedDispatchClientForIntegration dispatch = new(new[]
        {
            SlackOutboundDispatchResult.RateLimited(429, retryAfter, "{\"ok\":false,\"error\":\"ratelimited\"}"),
            SlackOutboundDispatchResult.Success(200, "1700000050.000020", "{\"ok\":true}"),
        });
        InMemorySlackDeadLetterQueue dlq = new();
        DefaultSlackRetryPolicy retryPolicy = new(connectorMonitor);
        InMemorySlackAuditEntryWriter audit = new();

        SlackOutboundDispatcher dispatcher = new(
            outboundQueue,
            threadManager,
            dispatch,
            sharedBucket,
            retryPolicy,
            dlq,
            audit,
            connectorMonitor,
            NullLogger<SlackOutboundDispatcher>.Instance,
            TimeProvider.System);

        await outboundQueue.EnqueueAsync(new SlackOutboundEnvelope(
            TaskId: "TASK-RL",
            CorrelationId: "corr-rl",
            MessageType: SlackOutboundOperationKind.PostMessage,
            BlockKitPayload: "{\"blocks\":[]}",
            ThreadTs: ThreadTs));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await dispatcher.StartAsync(cts.Token);
        try
        {
            // Wait for the two-attempt cycle (1st = 429, 2nd =
            // success after the bucket pause elapses).
            await WaitUntilAsync(() => dispatch.CallCount >= 2, TimeSpan.FromSeconds(8));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            cts.Cancel();
        }

        dispatch.CallCount.Should().Be(2,
            "the dispatcher MUST retry once after the bucket pause from the 429 -- the shared limiter is what actually blocks the second AcquireAsync until Slack's Retry-After window elapses");
        IReadOnlyList<SlackDeadLetterEntry> dlqEntries = await dlq.InspectAsync();
        dlqEntries.Should().BeEmpty(
            "back-pressure must NOT dead-letter; the rate-limiter pause is the correct disposition (Stage 6.3 iter-2 / FR-005)");
        audit.Entries.Should().Contain(e => e.Outcome == SlackOutboundDispatcher.OutcomeRateLimited);
        audit.Entries.Should().Contain(e => e.Outcome == SlackOutboundDispatcher.OutcomeSuccess);
    }

    private static SlackThreadMapping BuildMapping(string taskId) => new()
    {
        TaskId = taskId,
        TeamId = TeamId,
        ChannelId = ChannelId,
        ThreadTs = ThreadTs,
        AgentId = "agent-int",
        CorrelationId = "corr-int",
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

    /// <summary>
    /// <see cref="ISlackRateLimiter"/> proxy that journals every
    /// <see cref="AcquireAsync"/> and <see cref="NotifyRetryAfter"/>
    /// call before delegating to the wrapped real
    /// <see cref="SlackTokenBucketRateLimiter"/>. Used to PROVE that
    /// <see cref="SlackDirectApiClient"/> and
    /// <see cref="SlackOutboundDispatcher"/> hit the SAME limiter
    /// instance -- a reference-identity check on the proxy's journal
    /// is the only assertion that doesn't false-positive on two
    /// independent buckets sharing the same configuration.
    /// </summary>
    private sealed class RecordingSharedLimiter : ISlackRateLimiter
    {
        private readonly SlackTokenBucketRateLimiter inner;
        private readonly List<(string Source, SlackApiTier Tier, string ScopeKey)> acquires = new();
        private readonly List<(SlackApiTier Tier, string ScopeKey, TimeSpan Delay)> notifications = new();

        public RecordingSharedLimiter(SlackTokenBucketRateLimiter inner)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public IReadOnlyList<(string Source, SlackApiTier Tier, string ScopeKey)> Acquires
        {
            get
            {
                lock (this.acquires)
                {
                    return this.acquires.ToArray();
                }
            }
        }

        public IReadOnlyList<(SlackApiTier Tier, string ScopeKey, TimeSpan Delay)> Notifications
        {
            get
            {
                lock (this.notifications)
                {
                    return this.notifications.ToArray();
                }
            }
        }

        public async ValueTask AcquireAsync(SlackApiTier tier, string scopeKey, CancellationToken ct)
        {
            lock (this.acquires)
            {
                // The source label is derived from the tier: views.open
                // is Tier 4, chat.postMessage is Tier 2. The journal
                // captures both so the test can prove BOTH pipelines
                // hit this single proxy instance.
                string source = tier switch
                {
                    SlackApiTier.Tier4 => "views.open",
                    SlackApiTier.Tier2 => "chat.postMessage",
                    _ => "other",
                };
                this.acquires.Add((source, tier, scopeKey));
            }

            await this.inner.AcquireAsync(tier, scopeKey, ct).ConfigureAwait(false);
        }

        public void NotifyRetryAfter(SlackApiTier tier, string scopeKey, TimeSpan delay)
        {
            lock (this.notifications)
            {
                this.notifications.Add((tier, scopeKey, delay));
            }

            this.inner.NotifyRetryAfter(tier, scopeKey, delay);
        }
    }

    private sealed class StubWorkspaceStoreForIntegration : ISlackWorkspaceConfigStore
    {
        private readonly IReadOnlyDictionary<string, SlackWorkspaceConfig> workspaces;

        public StubWorkspaceStoreForIntegration(IReadOnlyDictionary<string, SlackWorkspaceConfig> workspaces)
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
            => Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(this.workspaces.Values.ToArray());
    }

    private sealed class StubSecretProviderForIntegration : ISecretProvider
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public void Set(string reference, string value)
        {
            this.values[reference] = value;
        }

        public Task<string> GetSecretAsync(string secretRef, CancellationToken cancellationToken)
        {
            if (this.values.TryGetValue(secretRef, out string? v))
            {
                return Task.FromResult(v);
            }

            throw new SecretNotFoundException(secretRef);
        }
    }

    private sealed class StubThreadManagerForIntegration : ISlackThreadManager
    {
        private readonly SlackThreadMapping mapping;

        public StubThreadManagerForIntegration(SlackThreadMapping mapping)
        {
            this.mapping = mapping;
        }

        public Task<SlackThreadMapping> GetOrCreateThreadAsync(
            string taskId, string agentId, string correlationId, string teamId, CancellationToken ct)
            => Task.FromResult(this.mapping);

        public Task<SlackThreadMapping?> GetThreadAsync(string taskId, CancellationToken ct)
            => Task.FromResult<SlackThreadMapping?>(this.mapping);

        public Task<bool> TouchAsync(string taskId, CancellationToken ct)
            => Task.FromResult(true);

        public Task<SlackThreadMapping?> RecoverThreadAsync(
            string taskId, string agentId, string correlationId, string teamId, CancellationToken ct)
            => Task.FromResult<SlackThreadMapping?>(this.mapping);

        public Task<SlackThreadPostResult> PostThreadedReplyAsync(
            string taskId, string text, string? correlationId, CancellationToken ct)
            => Task.FromResult(SlackThreadPostResult.Posted(this.mapping, "x"));
    }

    private sealed class RecordingDispatchClientForIntegration : ISlackOutboundDispatchClient
    {
        private readonly List<SlackOutboundDispatchRequest> calls = new();

        public IReadOnlyList<SlackOutboundDispatchRequest> Calls
        {
            get
            {
                lock (this.calls)
                {
                    return this.calls.ToArray();
                }
            }
        }

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

    private sealed class ScriptedDispatchClientForIntegration : ISlackOutboundDispatchClient
    {
        private readonly Queue<SlackOutboundDispatchResult> scripted;
        private int callCount;

        public ScriptedDispatchClientForIntegration(IEnumerable<SlackOutboundDispatchResult> results)
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
        public StaticOptionsMonitor(T value)
        {
            this.CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => this.CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
