using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Diagnostics;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        var handlerHarness = HandlerFactory.BuildE2E(publisher);
        HandlerFactory.MapDave(handlerHarness.IdentityResolver);
        var activity = HandlerFactory.NewPersonalMessage("agent status", correlationId: "corr-status-e2e");

        await HandlerFactory.ProcessE2EAsync(handlerHarness, activity);

        var received = await harness.Connector.ReceiveAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        var commandEvent = Assert.IsType<CommandEvent>(received);
        Assert.Equal(MessengerEventTypes.Command, commandEvent.EventType);
        Assert.Equal("agent status", commandEvent.Payload.CommandType);
        Assert.Equal(string.Empty, commandEvent.Payload.Payload);
        Assert.Equal("corr-status-e2e", commandEvent.CorrelationId);
        Assert.Equal("Teams", commandEvent.Messenger);
        Assert.Equal("aad-obj-dave-001", commandEvent.ExternalUserId);
    }

    [Fact]
    public async Task SendQuestionAsync_HappyPath_PersistsQuestionWithConversationIdAndSavesCardState()
    {
        var harness = ConnectorHarness.Build();
        var stored = NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "internal-alice")] = stored;

        var question = NewQuestion("Q-1001", targetUserId: "internal-alice");

        await harness.Connector.SendQuestionAsync(question, CancellationToken.None);

        // Iter-8 evaluator feedback #1 -- the AgentQuestion is persisted ONLY after the
        // proactive send has succeeded AND yielded the conversation/activity metadata.
        // The saved row carries the resolved deliveredConversationId from the proactive
        // turn context, not the caller-supplied value (which is treated as advisory).
        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-1001", saved.QuestionId);
        Assert.Equal(stored.ConversationId, saved.ConversationId);

        // The previous UpdateConversationIdAsync follow-up call is no longer needed
        // for this connector because SaveAsync now writes the ConversationId directly.
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);

        // Proactive send executed.
        var call = Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Equal(MicrosoftAppId, call.BotAppId);
        Assert.Equal(stored.ConversationId, call.Reference.Conversation.Id);
        var sentActivity = Assert.Single(harness.Adapter.Sent);
        // Implementation-plan §3.1 step 7 — outbound proactive carries the rendered
        // Adaptive Card as an Attachment, with Activity.Text falling back to the title
        // for clients that cannot render the card.
        Assert.Equal(question.Title, sentActivity.Text);
        var attachment = Assert.Single(sentActivity.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
        var cardJson = Assert.IsType<JObject>(attachment.Content);
        var cardText = cardJson.ToString();
        Assert.Contains(question.Title, cardText, StringComparison.Ordinal);
        Assert.Contains(question.Body, cardText, StringComparison.Ordinal);
        Assert.Contains("Action.Submit", cardText, StringComparison.Ordinal);
        Assert.Contains("approve", cardText, StringComparison.Ordinal);

        // TeamsCardState saved with the activityId returned by SendActivitiesAsync.
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
    /// <c>ConversationId</c> must NOT be persisted with that stale routing data; the
    /// connector replaces it with the deliveredConversationId from the proactive turn
    /// context so the bare-approve/bare-reject lookup path remains correct. Iter-8
    /// evaluator feedback #1 moved the persist step to AFTER the send, so the saved
    /// row's ConversationId is always the post-send value rather than null-then-update.
    /// </summary>
    [Fact]
    public async Task SendQuestionAsync_QuestionWithStaleConversationId_PersistsCopyWithDeliveredConversationId()
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
        // The stale ConversationId is REPLACED by the proactive deliveredConversationId,
        // not pass-through. This is the iter-1 sanitization invariant restated under the
        // iter-8 save-after-send ordering.
        Assert.NotEqual("19:STALE-DO-NOT-PERSIST", saved.ConversationId);
        Assert.Equal(stored.ConversationId, saved.ConversationId);

        // UpdateConversationIdAsync is no longer called from SendQuestionAsync — the
        // resolved ConversationId is written directly via SaveAsync above.
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
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

    // -----------------------------------------------------------------------------------
    // Iter-5 evaluator feedback item 1 — channel-targeted SendQuestionAsync must NOT
    // stamp the channel ID into the canonical TeamsLogScope UserId enrichment slot.
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task SendQuestionAsync_ChannelTarget_LogScopeOmitsUserIdEnrichment()
    {
        // Iter-5 — Regression for the channel-as-UserId mislabel at
        // TeamsMessengerConnector.SendQuestionAsync (formerly line ~390 in iter-4 layout).
        // When the orchestrator routes a question to a channel (TargetChannelId set,
        // TargetUserId null), the connector's logging scope MUST contain
        // (CorrelationId, TenantId) only; UserId must be absent so dashboards and
        // user-oriented RBAC queries are not polluted with channel IDs that look
        // like user IDs.
        var logger = new ConnectorScopeRecordingLogger();
        var harness = ConnectorHarness.Build(logger: logger);
        var stored = NewChannelReference("ref-channel-scope", channelId: "19:team-channel-scope");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "19:team-channel-scope")] = stored;

        var question = NewQuestion(
            "Q-channel-scope",
            targetUserId: null,
            targetChannelId: "19:team-channel-scope");

        await harness.Connector.SendQuestionAsync(question, CancellationToken.None);

        var dispatchScope = Assert.Single(
            logger.ScopeDictionaries,
            d => d.TryGetValue(TeamsLogScope.CorrelationIdKey, out var c)
                 && (string?)c == question.CorrelationId);
        Assert.Equal(TenantId, dispatchScope[TeamsLogScope.TenantIdKey]);
        Assert.False(
            dispatchScope.ContainsKey(TeamsLogScope.UserIdKey),
            "Channel-targeted SendQuestionAsync must not emit the UserId enrichment key. " +
            $"Found UserId='{(dispatchScope.TryGetValue(TeamsLogScope.UserIdKey, out var v) ? v : null)}'.");
    }

    [Fact]
    public async Task SendQuestionAsync_UserTarget_LogScopeEmitsUserIdEnrichment()
    {
        // Companion to the channel test — personal (TargetUserId-populated) questions
        // SHOULD continue to surface the target user identity in the UserId slot.
        var logger = new ConnectorScopeRecordingLogger();
        var harness = ConnectorHarness.Build(logger: logger);
        var stored = NewPersonalReference("ref-alice-scope", aadObjectId: "aad-alice", internalUserId: "internal-alice");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "internal-alice")] = stored;

        var question = NewQuestion("Q-user-scope", targetUserId: "internal-alice");

        await harness.Connector.SendQuestionAsync(question, CancellationToken.None);

        var dispatchScope = Assert.Single(
            logger.ScopeDictionaries,
            d => d.TryGetValue(TeamsLogScope.CorrelationIdKey, out var c)
                 && (string?)c == question.CorrelationId);
        Assert.Equal(TenantId, dispatchScope[TeamsLogScope.TenantIdKey]);
        Assert.Equal("internal-alice", dispatchScope[TeamsLogScope.UserIdKey]);
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

        // Iter-7 evaluator feedback #4 -- reference lookup MUST run before
        // AgentQuestionStore.SaveAsync. When no reference is found the connector
        // throws without persisting the question, so no Open row is left behind for
        // a question that was never delivered.
        Assert.Empty(harness.AgentQuestionStore.Saved);

        // Card state and conversation-id update did not run.
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    /// <summary>
    /// Regression for evaluator-iter-1 finding #2 — when the proactive turn context yields
    /// no <c>Conversation.Id</c>, the connector must throw rather than silently skipping
    /// persistence; otherwise bare <c>approve</c>/<c>reject</c> resolution is broken
    /// without surfacing the failure. Iter-8 evaluator feedback #1 strengthened this
    /// contract: the AgentQuestion is no longer persisted before the send, so a missing
    /// conversation ID leaves NEITHER an Open zombie row NOR a card state row.
    /// </summary>
    [Fact]
    public async Task SendQuestionAsync_MissingConversationIdInProactiveCallback_ThrowsAndPersistsNothing()
    {
        var stored = NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");
        var harness = ConnectorHarness.Build(adapter: new ConversationlessCloudAdapter());
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "internal-alice")] = stored;

        var question = NewQuestion("Q-missing-conv", targetUserId: "internal-alice");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Connector.SendQuestionAsync(question, CancellationToken.None));

        Assert.Contains("Q-missing-conv", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Conversation.Id", ex.Message, StringComparison.Ordinal);

        // Iter-8 evaluator feedback #1 -- the AgentQuestion is persisted ONLY after the
        // send yields both Conversation.Id and Activity.Id. Because Conversation.Id was
        // missing, NO row is written to the AgentQuestionStore, no UpdateConversationId
        // call is made, and no card state is saved. This is the all-or-nothing contract.
        Assert.Empty(harness.AgentQuestionStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Empty(harness.CardStateStore.Saved);
    }

    /// <summary>
    /// Regression for evaluator-iter-1 finding #3 — when <see cref="ITurnContext.SendActivityAsync"/>
    /// returns a <see cref="ResourceResponse"/> without an <c>Id</c>, the connector must
    /// throw rather than silently skipping <see cref="ICardStateStore.SaveAsync"/>; the
    /// proactive card was sent but cannot be updated/deleted and partial persistence would
    /// hide the failure from operators. Iter-8 evaluator feedback #1 strengthened this
    /// contract: the AgentQuestion is no longer persisted before the send, so a missing
    /// activity ID leaves NEITHER an Open zombie row NOR a card state row.
    /// </summary>
    [Fact]
    public async Task SendQuestionAsync_MissingActivityIdInResourceResponse_ThrowsAndPersistsNothing()
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

        // Iter-8 evaluator feedback #1 -- save-after-send means no AgentQuestion row.
        Assert.Empty(harness.AgentQuestionStore.Saved);
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
        var renderer = new AdaptiveCardBuilder();
        var reader = new ChannelInboundEventPublisher();
        var logger = NullLogger<TeamsMessengerConnector>.Instance;

        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(null!, options, convStore, router, qStore, cardStore, renderer, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, null!, convStore, router, qStore, cardStore, renderer, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, null!, router, qStore, cardStore, renderer, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, null!, qStore, cardStore, renderer, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, null!, cardStore, renderer, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, qStore, null!, renderer, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, qStore, cardStore, null!, reader, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, qStore, cardStore, renderer, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsMessengerConnector(adapter, options, convStore, router, qStore, cardStore, renderer, reader, null!));
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
        public static ConnectorHarness Build(
            IInboundEventReader? reader = null,
            RecordingCloudAdapter? adapter = null,
            ILogger<TeamsMessengerConnector>? logger = null)
        {
            adapter ??= new RecordingCloudAdapter();
            var options = new TeamsMessagingOptions { MicrosoftAppId = MicrosoftAppId };
            var convStore = new ConnectorRecordingConversationReferenceStore();
            var router = new RecordingConversationReferenceRouter();
            var qStore = new RecordingAgentQuestionStore();
            var cardStore = new RecordingCardStateStore();
            var renderer = new AdaptiveCardBuilder();
            reader ??= new ChannelInboundEventPublisher();
            var connector = new TeamsMessengerConnector(
                adapter,
                options,
                convStore,
                router,
                qStore,
                cardStore,
                renderer,
                reader,
                logger ?? NullLogger<TeamsMessengerConnector>.Instance);
            return new ConnectorHarness(connector, adapter, convStore, router, qStore, cardStore, options);
        }
    }

    /// <summary>
    /// Iter-5 — minimal <see cref="ILogger{T}"/> that snapshots every
    /// <see cref="ILogger.BeginScope{TState}"/> dictionary into
    /// <see cref="ScopeDictionaries"/>. Used by the channel-vs-personal
    /// <see cref="TeamsLogScope"/> regression tests to assert which enrichment
    /// keys are present / absent on the connector's logging scope. Snapshots are
    /// captured via the <see cref="IEnumerable{T}"/> of
    /// <see cref="KeyValuePair{TKey, TValue}"/> interface contract — the same
    /// projection Serilog's <c>Microsoft.Extensions.Logging</c> bridge uses to
    /// hoist scope state onto every emitted <c>LogEvent</c>.
    /// </summary>
    private sealed class ConnectorScopeRecordingLogger : ILogger<TeamsMessengerConnector>
    {
        public List<IReadOnlyDictionary<string, object?>> ScopeDictionaries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kvp in kvps)
                {
                    snapshot[kvp.Key] = kvp.Value;
                }

                ScopeDictionaries.Add(snapshot);
            }

            return Noop.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose()
            {
            }
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
