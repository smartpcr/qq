using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Verifies the three Stage 2.3 test scenarios from <c>implementation-plan.md</c> §2.3 plus
/// the <see cref="TeamsMessengerConnector.SendQuestionAsync"/> three-step persistence
/// contract.
/// </summary>
public sealed class TeamsMessengerConnectorTests
{
    private const string TenantId = "contoso-tenant-id";
    private const string MicrosoftAppId = "11111111-1111-1111-1111-111111111111";
    private const string ConversationId = "19:conversation-dave";

    [Fact]
    public async Task SendMessageAsync_StoredReference_InvokesContinueConversationAsyncAndDelivers()
    {
        var harness = ConnectorHarness.Build();
        var stored = NewPersonalReference("ref-1", aadObjectId: "aad-dave", internalUserId: "internal-dave");
        harness.ConversationReferenceRouter.PreloadByConversationId[ConversationId] = stored;

        var message = new MessengerMessage(
            MessageId: "msg-1",
            CorrelationId: "corr-1",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: ConversationId,
            Body: "Build complete on stage-3.1.",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await harness.Connector.SendMessageAsync(message, CancellationToken.None);

        var call = Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Equal(MicrosoftAppId, call.BotAppId);
        Assert.Equal(stored.ConversationId, call.Reference.Conversation.Id);
        Assert.Equal(stored.ServiceUrl, call.Reference.ServiceUrl);

        var sent = Assert.Single(harness.Adapter.Sent);
        Assert.Equal(ActivityTypes.Message, sent.Type);
        Assert.Equal(message.Body, sent.Text);

        Assert.Equal(ConversationId, Assert.Single(harness.ConversationReferenceRouter.Lookups));
    }

    [Fact]
    public async Task SendMessageAsync_NoStoredReference_ThrowsInvalidOperationException()
    {
        var harness = ConnectorHarness.Build();
        var missingConversationId = "19:does-not-exist";
        var message = new MessengerMessage(
            MessageId: "msg-2",
            CorrelationId: "corr-2",
            AgentId: "agent-build",
            TaskId: "task-43",
            ConversationId: missingConversationId,
            Body: "Should fail.",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Connector.SendMessageAsync(message, CancellationToken.None));

        Assert.Contains(missingConversationId, ex.Message, StringComparison.Ordinal);
        Assert.Contains("msg-2", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
    }

    [Fact]
    public async Task SendMessageAsync_EmptyConversationId_ThrowsInvalidOperationException()
    {
        var harness = ConnectorHarness.Build();
        var message = new MessengerMessage(
            MessageId: "msg-empty",
            CorrelationId: "corr-empty",
            AgentId: "agent-build",
            TaskId: "task-44",
            ConversationId: string.Empty,
            Body: "Body",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Connector.SendMessageAsync(message, CancellationToken.None));

        Assert.Empty(harness.ConversationReferenceRouter.Lookups);
    }

    [Fact]
    public async Task SendMessageAsync_NullMessage_ThrowsArgumentNullException()
    {
        var harness = ConnectorHarness.Build();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Connector.SendMessageAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// End-to-end ReceiveAsync coverage for the §2.3 brief scenario:
    /// "Given the activity handler processes an `agent status` message,
    /// When ReceiveAsync is awaited, Then a CommandEvent with the parsed status command is returned."
    /// Wires a real <see cref="TeamsSwarmActivityHandler"/> into the same
    /// <see cref="ChannelInboundEventPublisher"/> instance the connector reads from, so the
    /// inbound activity flows through identity → authorization → publish → channel →
    /// connector.ReceiveAsync without any test-side shortcuts.
    /// </summary>
    [Fact]
    public async Task ReceiveAsync_AfterHandlerProcessesAgentStatusActivity_ReturnsCommandEventForCanonicalVerb()
    {
        var publisher = new ChannelInboundEventPublisher();
        var harness = ConnectorHarness.Build(reader: publisher);

        var handlerHarness = HandlerFactory.Build(publisher);
        HandlerFactory.MapDave(handlerHarness.IdentityResolver);
        var activity = HandlerFactory.NewPersonalMessage("agent status", correlationId: "corr-status-e2e");

        await HandlerFactory.ProcessAsync(handlerHarness, activity);

        var received = await harness.Connector.ReceiveAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        var commandEvent = Assert.IsType<CommandEvent>(received);
        Assert.Equal(MessengerEventTypes.Command, commandEvent.EventType);
        Assert.Equal("agent status", commandEvent.Payload.CommandType);
        Assert.Equal(string.Empty, commandEvent.Payload.Payload);
        Assert.Equal("corr-status-e2e", commandEvent.CorrelationId);
        Assert.Equal("Teams", commandEvent.Messenger);
        Assert.Equal("aad-obj-dave-001", commandEvent.ExternalUserId);
        Assert.Equal(MessengerEventSources.PersonalChat, commandEvent.Source);
        Assert.Equal(activity.Id, commandEvent.ActivityId);
    }

    [Fact]
    public async Task SendQuestionAsync_HappyPath_PersistsQuestionUpdatesConversationIdAndSavesCardState()
    {
        var harness = ConnectorHarness.Build();
        var stored = NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "internal-alice")] = stored;

        var question = NewQuestion("Q-1001", targetUserId: "internal-alice");

        await harness.Connector.SendQuestionAsync(question, CancellationToken.None);

        // Step 1 — question persisted before send.
        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-1001", saved.QuestionId);
        Assert.Null(saved.ConversationId); // sanitization invariant — see SendQuestionAsync_StaleConversationId test below.

        // Step 2 — proactive send executed.
        var call = Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Equal(MicrosoftAppId, call.BotAppId);
        Assert.Equal(stored.ConversationId, call.Reference.Conversation.Id);
        var sentText = Assert.Single(harness.Adapter.Sent).Text ?? string.Empty;
        Assert.Contains(question.Title, sentText, StringComparison.Ordinal);
        Assert.Contains(question.Body, sentText, StringComparison.Ordinal);
        Assert.Contains("approve", sentText, StringComparison.Ordinal);

        // Step 3 — question's ConversationId stamped from the proactive turn context, and
        // TeamsCardState saved with the activityId returned by SendActivitiesAsync.
        var update = Assert.Single(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Equal("Q-1001", update.QuestionId);
        Assert.Equal(stored.ConversationId, update.ConversationId);

        var cardState = Assert.Single(harness.CardStateStore.Saved);
        Assert.Equal("Q-1001", cardState.QuestionId);
        Assert.False(string.IsNullOrWhiteSpace(cardState.ActivityId));
        Assert.Equal(stored.ConversationId, cardState.ConversationId);
        Assert.Equal(TeamsCardStatuses.Pending, cardState.Status);
        Assert.False(string.IsNullOrWhiteSpace(cardState.ConversationReferenceJson));

        // Card state's ConversationReferenceJson must round-trip through Newtonsoft so the
        // Stage 4.x proactive worker can rehydrate it.
        var rehydrated = JsonConvert.DeserializeObject<ConversationReference>(cardState.ConversationReferenceJson);
        Assert.NotNull(rehydrated);
        Assert.Equal(stored.ConversationId, rehydrated!.Conversation.Id);
    }

    /// <summary>
    /// Regression for evaluator-iter-1 finding #1 — a question arriving with a non-null
    /// <c>ConversationId</c> must not be persisted with that stale routing data; the
    /// connector saves a sanitized copy with <c>ConversationId = null</c> so the
    /// bare-approve/bare-reject lookup path remains correct (the field is later stamped
    /// with the actual proactive ConversationId in step 3).
    /// </summary>
    [Fact]
    public async Task SendQuestionAsync_QuestionWithStaleConversationId_PersistsSanitizedCopyWithNullConversationId()
    {
        var harness = ConnectorHarness.Build();
        var stored = NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "internal-alice")] = stored;

        var staleQuestion = NewQuestion("Q-stale", targetUserId: "internal-alice") with
        {
            ConversationId = "19:STALE-DO-NOT-PERSIST",
        };

        await harness.Connector.SendQuestionAsync(staleQuestion, CancellationToken.None);

        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-stale", saved.QuestionId);
        Assert.Null(saved.ConversationId);
        Assert.NotEqual("19:STALE-DO-NOT-PERSIST", saved.ConversationId);

        // Step 3 still stamps the actual proactive ConversationId via UpdateConversationIdAsync.
        var update = Assert.Single(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Equal(stored.ConversationId, update.ConversationId);
    }

    [Fact]
    public async Task SendQuestionAsync_ChannelTarget_UsesGetByChannelIdLookup()
    {
        var harness = ConnectorHarness.Build();
        var stored = NewChannelReference("ref-channel", channelId: "19:team-channel");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "19:team-channel")] = stored;

        var question = NewQuestion("Q-2002", targetUserId: null, targetChannelId: "19:team-channel");

        await harness.Connector.SendQuestionAsync(question, CancellationToken.None);

        Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Equal((TenantId, "19:team-channel"), Assert.Single(harness.ConversationReferenceStore.ChannelLookups));
        Assert.Empty(harness.ConversationReferenceStore.InternalUserLookups);
    }

    [Fact]
    public async Task SendQuestionAsync_NoStoredReference_ThrowsInvalidOperationException()
    {
        var harness = ConnectorHarness.Build();
        var question = NewQuestion("Q-3003", targetUserId: "internal-bob");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Connector.SendQuestionAsync(question, CancellationToken.None));

        Assert.Contains("Q-3003", ex.Message, StringComparison.Ordinal);
        Assert.Contains("internal-bob", ex.Message, StringComparison.Ordinal);

        // Question was still persisted (step 1 happens before reference resolution per
        // the §2.3 brief).
        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-3003", saved.QuestionId);

        // Card state and conversation-id update did not run.
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    /// <summary>
    /// Regression for evaluator-iter-1 finding #2 — when the proactive turn context yields
    /// no <c>Conversation.Id</c>, the connector must throw rather than silently skipping
    /// <see cref="IAgentQuestionStore.UpdateConversationIdAsync"/>; otherwise bare
    /// <c>approve</c>/<c>reject</c> resolution is broken without surfacing the failure.
    /// Card state must NOT be saved when conversation ID is missing, so callers can rely
    /// on the all-or-nothing persistence contract.
    /// </summary>
    [Fact]
    public async Task SendQuestionAsync_MissingConversationIdInProactiveCallback_ThrowsAndSkipsAllStep3Persistence()
    {
        var stored = NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");
        var harness = ConnectorHarness.Build(adapter: new ConversationlessCloudAdapter());
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "internal-alice")] = stored;

        var question = NewQuestion("Q-missing-conv", targetUserId: "internal-alice");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Connector.SendQuestionAsync(question, CancellationToken.None));

        Assert.Contains("Q-missing-conv", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Conversation.Id", ex.Message, StringComparison.Ordinal);

        // Step 1 ran before the failure.
        Assert.Single(harness.AgentQuestionStore.Saved);
        // Step 3 never ran on either side — no partial persistence.
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Empty(harness.CardStateStore.Saved);
    }

    /// <summary>
    /// Regression for evaluator-iter-1 finding #3 — when <see cref="ITurnContext.SendActivityAsync"/>
    /// returns a <see cref="ResourceResponse"/> without an <c>Id</c>, the connector must
    /// throw rather than silently skipping <see cref="ICardStateStore.SaveAsync"/>; the
    /// proactive card was sent but cannot be updated/deleted and partial persistence would
    /// hide the failure from operators.
    /// </summary>
    [Fact]
    public async Task SendQuestionAsync_MissingActivityIdInResourceResponse_ThrowsAndSkipsAllStep3Persistence()
    {
        var stored = NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");
        var adapter = new RecordingCloudAdapter
        {
            SendResponseFactory = _ => new ResourceResponse(id: string.Empty),
        };
        var harness = ConnectorHarness.Build(adapter: adapter);
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "internal-alice")] = stored;

        var question = NewQuestion("Q-missing-act", targetUserId: "internal-alice");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Connector.SendQuestionAsync(question, CancellationToken.None));

        Assert.Contains("Q-missing-act", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Activity.Id", ex.Message, StringComparison.Ordinal);

        Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Empty(harness.CardStateStore.Saved);
    }

    [Fact]
    public async Task SendQuestionAsync_InvalidQuestion_ThrowsBeforePersistence()
    {
        var harness = ConnectorHarness.Build();
        var invalid = new AgentQuestion
        {
            QuestionId = "Q-bad",
            AgentId = string.Empty, // invalid — required field
            TaskId = "task",
            TenantId = TenantId,
            TargetUserId = "internal-alice",
            Title = "T",
            Body = "B",
            Severity = MessageSeverities.Info,
            CorrelationId = "c",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            AllowedActions = new[] { new HumanAction("approve", "Approve", "approve", false) },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Connector.SendQuestionAsync(invalid, CancellationToken.None));

        Assert.Empty(harness.AgentQuestionStore.Saved);
        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    [Fact]
    public async Task SendQuestionAsync_NullQuestion_ThrowsArgumentNullException()
    {
        var harness = ConnectorHarness.Build();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Connector.SendQuestionAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullDependencies_AllThrowArgumentNullException()
    {
        var adapter = new RecordingCloudAdapter();
        var options = new TeamsMessagingOptions { MicrosoftAppId = MicrosoftAppId };
        var convStore = new ConnectorRecordingConversationReferenceStore();
        var router = new RecordingConversationReferenceRouter();
        var qStore = new RecordingAgentQuestionStore();
        var cardStore = new RecordingCardStateStore();
        var reader = new ChannelInboundEventPublisher();
        var logger = NullLogger<TeamsMessengerConnector>.Instance;

        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(null!, options, convStore, router, qStore, cardStore, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, null!, convStore, router, qStore, cardStore, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, null!, router, qStore, cardStore, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, null!, qStore, cardStore, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, null!, cardStore, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, qStore, null!, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, qStore, cardStore, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, qStore, cardStore, reader, null!));
    }

    private static TeamsConversationReference NewPersonalReference(
        string id,
        string aadObjectId,
        string internalUserId,
        string conversationId = ConversationId)
    {
        var bfReference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            Bot = new ChannelAccount(id: MicrosoftAppId, name: "AgentBot"),
            User = new ChannelAccount(id: $"29:{aadObjectId}", name: "User") { AadObjectId = aadObjectId },
            Conversation = new ConversationAccount(id: conversationId) { TenantId = TenantId },
        };

        return new TeamsConversationReference
        {
            Id = id,
            TenantId = TenantId,
            AadObjectId = aadObjectId,
            InternalUserId = internalUserId,
            ServiceUrl = bfReference.ServiceUrl,
            ConversationId = conversationId,
            BotId = MicrosoftAppId,
            ReferenceJson = JsonConvert.SerializeObject(bfReference),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
    }

    private static TeamsConversationReference NewChannelReference(
        string id,
        string channelId,
        string conversationId = "19:channel-conv")
    {
        var bfReference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            Bot = new ChannelAccount(id: MicrosoftAppId, name: "AgentBot"),
            Conversation = new ConversationAccount(id: conversationId) { TenantId = TenantId, ConversationType = "channel" },
        };

        return new TeamsConversationReference
        {
            Id = id,
            TenantId = TenantId,
            ChannelId = channelId,
            ServiceUrl = bfReference.ServiceUrl,
            ConversationId = conversationId,
            BotId = MicrosoftAppId,
            ReferenceJson = JsonConvert.SerializeObject(bfReference),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
    }

    private static AgentQuestion NewQuestion(
        string questionId,
        string? targetUserId = null,
        string? targetChannelId = null)
    {
        return new AgentQuestion
        {
            QuestionId = questionId,
            AgentId = "agent-build",
            TaskId = "task-42",
            TenantId = TenantId,
            TargetUserId = targetUserId,
            TargetChannelId = targetChannelId,
            Title = "Promote build to staging?",
            Body = "Build #42 finished. Promote to staging environment?",
            Severity = MessageSeverities.Info,
            AllowedActions = new[]
            {
                new HumanAction("approve", "Approve", "approve", false),
                new HumanAction("reject", "Reject", "reject", true),
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = $"corr-{questionId}",
        };
    }

    /// <summary>
    /// Wraps the connector together with all of its mock collaborators so individual tests
    /// stay focused on Arrange / Act / Assert.
    /// </summary>
    private sealed record ConnectorHarness(
        TeamsMessengerConnector Connector,
        RecordingCloudAdapter Adapter,
        ConnectorRecordingConversationReferenceStore ConversationReferenceStore,
        RecordingConversationReferenceRouter ConversationReferenceRouter,
        RecordingAgentQuestionStore AgentQuestionStore,
        RecordingCardStateStore CardStateStore,
        TeamsMessagingOptions Options)
    {
        public static ConnectorHarness Build(IInboundEventReader? reader = null, RecordingCloudAdapter? adapter = null)
        {
            adapter ??= new RecordingCloudAdapter();
            var options = new TeamsMessagingOptions { MicrosoftAppId = MicrosoftAppId };
            var convStore = new ConnectorRecordingConversationReferenceStore();
            var router = new RecordingConversationReferenceRouter();
            var qStore = new RecordingAgentQuestionStore();
            var cardStore = new RecordingCardStateStore();
            reader ??= new ChannelInboundEventPublisher();
            var connector = new TeamsMessengerConnector(
                adapter,
                options,
                convStore,
                router,
                qStore,
                cardStore,
                reader,
                NullLogger<TeamsMessengerConnector>.Instance);
            return new ConnectorHarness(connector, adapter, convStore, router, qStore, cardStore, options);
        }
    }

    /// <summary>
    /// <see cref="CloudAdapter"/> subclass that captures proactive calls and SendActivities
    /// invocations without touching the real Bot Framework Connector. Overrides
    /// <see cref="ContinueConversationAsync(string, ConversationReference, BotCallbackHandler, CancellationToken)"/>
    /// to record the invocation and execute the supplied callback against a synthesized
    /// <see cref="TurnContext"/>.
    /// </summary>
    public class RecordingCloudAdapter : CloudAdapter
    {
        public List<(string BotAppId, ConversationReference Reference)> ContinueCalls { get; } = new();
        public List<Activity> Sent { get; } = new();
        public Func<Activity, ResourceResponse>? SendResponseFactory { get; set; }

        public override Task ContinueConversationAsync(
            string botAppId,
            ConversationReference reference,
            BotCallbackHandler callback,
            CancellationToken cancellationToken)
        {
            ContinueCalls.Add((botAppId, reference));
            var continuation = SynthesizeContinuationActivity(reference);
            var turnContext = new TurnContext(this, continuation);
            return callback(turnContext, cancellationToken);
        }

        protected virtual Activity SynthesizeContinuationActivity(ConversationReference reference)
            => (Activity)reference.GetContinuationActivity();

        public override Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext,
            Activity[] activities,
            CancellationToken cancellationToken)
        {
            Sent.AddRange(activities);
            var responses = new ResourceResponse[activities.Length];
            for (var i = 0; i < activities.Length; i++)
            {
                responses[i] = SendResponseFactory?.Invoke(activities[i])
                    ?? new ResourceResponse(id: $"act-{Guid.NewGuid():N}");
            }

            return Task.FromResult(responses);
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
            => Task.FromResult(new ResourceResponse(activity.Id ?? Guid.NewGuid().ToString()));

        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Recording adapter that strips the <c>Conversation.Id</c> from the synthesized
    /// continuation activity so <see cref="TeamsMessengerConnector.SendQuestionAsync"/> sees
    /// a turn context with no resolvable conversation ID — used to drive the
    /// "fail loudly when proactive Conversation.Id is missing" regression.
    /// </summary>
    public sealed class ConversationlessCloudAdapter : RecordingCloudAdapter
    {
        protected override Activity SynthesizeContinuationActivity(ConversationReference reference)
        {
            var continuation = base.SynthesizeContinuationActivity(reference);
            continuation.Conversation = new ConversationAccount(id: null);
            return continuation;
        }
    }

    /// <summary>
    /// Recording <see cref="IConversationReferenceStore"/> tailored for connector tests.
    /// Distinct from <see cref="TestDoubles.RecordingConversationReferenceStore"/> (used by
    /// the Stage 2.2 activity-handler suite) because the Stage 2.3 connector exercises
    /// different lookup paths (by internal user, by channel).
    /// </summary>
    public sealed class ConnectorRecordingConversationReferenceStore : IConversationReferenceStore
    {
        public Dictionary<(string TenantId, string InternalUserId), TeamsConversationReference> PreloadByInternalUserId { get; } = new();
        public Dictionary<(string TenantId, string ChannelId), TeamsConversationReference> PreloadByChannelId { get; } = new();

        public List<(string TenantId, string InternalUserId)> InternalUserLookups { get; } = new();
        public List<(string TenantId, string ChannelId)> ChannelLookups { get; } = new();

        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);

        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
        {
            InternalUserLookups.Add((tenantId, internalUserId));
            PreloadByInternalUserId.TryGetValue((tenantId, internalUserId), out var hit);
            return Task.FromResult<TeamsConversationReference?>(hit);
        }

        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
        {
            ChannelLookups.Add((tenantId, channelId));
            PreloadByChannelId.TryGetValue((tenantId, channelId), out var hit);
            return Task.FromResult<TeamsConversationReference?>(hit);
        }

        public Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

        public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

        public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.FromResult(false);

        public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Recording <see cref="IAgentQuestionStore"/> that captures every call so the §2.3
    /// three-step persistence sequence can be asserted in order.
    /// </summary>
    public sealed class RecordingAgentQuestionStore : IAgentQuestionStore
    {
        public List<AgentQuestion> Saved { get; } = new();
        public List<(string QuestionId, string ConversationId)> ConversationIdUpdates { get; } = new();

        public Task SaveAsync(AgentQuestion question, CancellationToken ct)
        {
            Saved.Add(question);
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct) => Task.FromResult(false);

        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
        {
            ConversationIdUpdates.Add((questionId, conversationId));
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
        public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
    }

    /// <summary>
    /// Recording <see cref="ICardStateStore"/> for connector tests. Saved card-state
    /// rows are exposed via <see cref="Saved"/> for assertion.
    /// </summary>
    public sealed class RecordingCardStateStore : ICardStateStore
    {
        public List<TeamsCardState> Saved { get; } = new();
        public List<(string QuestionId, string Status)> StatusUpdates { get; } = new();
        public Dictionary<string, TeamsCardState> Preload { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(TeamsCardState state, CancellationToken ct)
        {
            Saved.Add(state);
            return Task.CompletedTask;
        }

        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
        {
            Preload.TryGetValue(questionId, out var hit);
            return Task.FromResult<TeamsCardState?>(hit);
        }

        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
        {
            StatusUpdates.Add((questionId, newStatus));
            return Task.CompletedTask;
        }
    }
}
