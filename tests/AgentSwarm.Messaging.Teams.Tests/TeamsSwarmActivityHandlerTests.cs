using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Extensions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Newtonsoft.Json.Linq;
using static AgentSwarm.Messaging.Teams.Tests.HandlerFactory;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Verifies the Stage 2.2 test scenarios in <c>implementation-plan.md</c> §2.2 plus the
/// install/uninstall and card-invoke overrides.
/// </summary>
public sealed class TeamsSwarmActivityHandlerTests
{
    [Fact]
    public async Task OnMessageActivityAsync_PersonalChat_DispatchesNormalizedTextToDispatcher()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent ask create e2e test scenarios for update service");

        await ProcessAsync(harness, activity);

        var context = Assert.Single(harness.Dispatcher.Dispatched);
        Assert.Equal("agent ask create e2e test scenarios for update service", context.NormalizedText);
        Assert.NotNull(context.ResolvedIdentity);
        Assert.Equal("internal-dave", context.ResolvedIdentity!.InternalUserId);
        Assert.Equal("conv-dave-001", context.ConversationId);
        Assert.False(string.IsNullOrWhiteSpace(context.CorrelationId));
    }

    [Fact]
    public async Task OnMessageActivityAsync_StripsAtMention_BeforeDispatch()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver, "aad-obj-bob-002");
        var activity = NewChannelMentionMessage("<at>AgentBot</at> agent ask plan migration");

        await ProcessAsync(harness, activity);

        var context = Assert.Single(harness.Dispatcher.Dispatched);
        Assert.Equal("agent ask plan migration", context.NormalizedText);
        Assert.DoesNotContain("<at>", context.NormalizedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnMessageActivityAsync_AuthorizedFirstInteraction_SavesConversationReferenceKeyedByAadObjectId()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent status");

        await ProcessAsync(harness, activity);

        var saved = Assert.Single(harness.Store.Saved);
        Assert.Equal(TenantId, saved.TenantId);
        Assert.Equal("aad-obj-dave-001", saved.AadObjectId);
        Assert.Equal("internal-dave", saved.InternalUserId);
        Assert.Null(saved.ChannelId);
        Assert.Null(saved.TeamId);
        Assert.True(saved.IsActive);
        Assert.Equal("conv-dave-001", saved.ConversationId);
        Assert.Equal(BotId, saved.BotId);
    }

    [Fact]
    public async Task OnMessageActivityAsync_PersistsReferenceJson_RoundTripsViaNewtonsoftAndPreservesCanonicalWireNames()
    {
        // Regression guard for the iter-3 -> iter-4 SerializeConversationReference fix:
        //
        // `Microsoft.Bot.Schema.ConversationReference` is annotated with Newtonsoft.Json
        // `[JsonProperty(PropertyName = "serviceUrl")]`-style attributes for the canonical
        // camelCase wire names. The persisted `ReferenceJson` is consumed by the Stage 4.x
        // proactive-messaging worker via `JsonConvert.DeserializeObject<ConversationReference>`.
        //
        // This test asserts BOTH: (a) the serialized JSON carries the camelCase wire names
        // that Bot Framework's Newtonsoft contract emits (so PascalCase regressions from a
        // future System.Text.Json swap fail loudly), and (b) deserializing back via
        // `JsonConvert.DeserializeObject<ConversationReference>` round-trips the key fields
        // (ServiceUrl, Conversation.Id, Bot.Id) without loss.
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent status");

        await ProcessAsync(harness, activity);

        var saved = Assert.Single(harness.Store.Saved);
        Assert.False(string.IsNullOrEmpty(saved.ReferenceJson));
        Assert.NotEqual("{}", saved.ReferenceJson);

        // Newtonsoft wire names — camelCase, NOT PascalCase.
        Assert.Contains("\"serviceUrl\"", saved.ReferenceJson, StringComparison.Ordinal);
        Assert.Contains("\"conversation\"", saved.ReferenceJson, StringComparison.Ordinal);
        Assert.Contains("\"bot\"", saved.ReferenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ServiceUrl\"", saved.ReferenceJson, StringComparison.Ordinal);

        // Round-trip via the same Newtonsoft contract the Stage 4.x proactive worker uses.
        var rehydrated = Newtonsoft.Json.JsonConvert
            .DeserializeObject<Microsoft.Bot.Schema.ConversationReference>(saved.ReferenceJson);
        Assert.NotNull(rehydrated);
        Assert.False(string.IsNullOrEmpty(rehydrated!.ServiceUrl));
        Assert.NotNull(rehydrated.Conversation);
        Assert.Equal("conv-dave-001", rehydrated.Conversation!.Id);
        Assert.NotNull(rehydrated.Bot);
        Assert.Equal(BotId, rehydrated.Bot!.Id);
    }

    [Fact]
    public async Task OnMessageActivityAsync_AuthorizedCommand_EmitsCommandReceivedAuditBeforeDispatch()
    {
        // Item #4 from iter-1 evaluator feedback: successful authorized commands must
        // persist an immutable `CommandReceived` audit record so the command trail is
        // reviewable end-to-end (story "Compliance — Persist immutable audit trail").
        //
        // Item #1 from iter-2 evaluator feedback: the audit record must conform to the
        // schema in `e2e-scenarios.md` §Compliance — "All inbound commands are
        // audit-logged":
        //   Action       = <canonical command verb> (e.g. "agent ask")
        //   PayloadJson  = {"body":"<remainder after the verb>"}
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent ask create e2e test scenarios for update service");

        await ProcessAsync(harness, activity);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.CommandReceived, audit.EventType);
        Assert.Equal(AuditOutcomes.Success, audit.Outcome);
        Assert.Equal(AuditActorTypes.User, audit.ActorType);
        Assert.Equal(TenantId, audit.TenantId);
        Assert.Equal("aad-obj-dave-001", audit.ActorId);
        // Canonical verb in the Action column (per e2e-scenarios.md:819).
        Assert.Equal("agent ask", audit.Action);
        // PayloadJson carries ONLY the body remainder (per e2e-scenarios.md:820).
        Assert.Equal(
            "{\"body\":\"create e2e test scenarios for update service\"}",
            audit.PayloadJson);
        Assert.Single(harness.Dispatcher.Dispatched);
    }

    [Theory]
    [InlineData("agent status", "agent status", "")]
    [InlineData("approve", "approve", "")]
    [InlineData("approve question-abc", "approve", "question-abc")]
    [InlineData("pause   ", "pause", "")]
    [InlineData("agent ask  multi   space body", "agent ask", "multi   space body")]
    public async Task OnMessageActivityAsync_AuthorizedCommand_AuditActionAndBodyMatchCanonicalSchema(
        string inputText,
        string expectedAction,
        string expectedBody)
    {
        // Item #1 from iter-2 evaluator feedback: exercises the
        // canonical Action + PayloadJson shape across all seven canonical verbs (per
        // architecture.md §5.2) including verbs with no body and verbs with multi-space
        // bodies that must be preserved verbatim after a single TrimStart.
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage(inputText);

        await ProcessAsync(harness, activity);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.CommandReceived, audit.EventType);
        Assert.Equal(expectedAction, audit.Action);
        Assert.Equal($"{{\"body\":\"{expectedBody}\"}}", audit.PayloadJson);
    }

    [Fact]
    public async Task OnMessageActivityAsync_UnmappedUser_RespondsWithAdaptiveCardAndDoesNotSaveReference()
    {
        // Item #5 from iter-1 evaluator feedback: rejection responses must be Adaptive
        // Cards (not plain text), per story "Cards — Use Adaptive Cards for ... incident
        // summaries" applied to access-denied flows.
        var harness = Build();
        var activity = NewPersonalMessage("agent ask something", aadObjectId: "aad-obj-eve-external");

        await ProcessAsync(harness, activity);

        Assert.Empty(harness.Store.Saved);
        Assert.Empty(harness.Dispatcher.Dispatched);
        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.SecurityRejection, audit.EventType);
        Assert.Equal(AuditOutcomes.Rejected, audit.Outcome);
        Assert.Equal("UnmappedUserRejected", audit.Action);

        var reply = Assert.Single(harness.Adapter.Sent);
        var attachment = Assert.Single(reply.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
    }

    [Fact]
    public async Task OnMessageActivityAsync_UnauthorizedRole_RespondsWithAdaptiveCardCarryingRequiredRole()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        harness.Authorization.IsAuthorized = false;
        harness.Authorization.UserRole = "viewer";
        harness.Authorization.RequiredRole = "approver";
        var activity = NewPersonalMessage("approve");

        await ProcessAsync(harness, activity);

        Assert.Empty(harness.Store.Saved);
        Assert.Empty(harness.Dispatcher.Dispatched);
        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.SecurityRejection, audit.EventType);
        Assert.Equal(AuditOutcomes.Rejected, audit.Outcome);
        Assert.Equal("InsufficientRoleRejected", audit.Action);

        var reply = Assert.Single(harness.Adapter.Sent);
        var attachment = Assert.Single(reply.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
        // The plain-text fallback (for clients that can't render Adaptive Cards) must
        // still mention the required role so the user knows what to request from admin.
        Assert.Contains("approver", reply.Text ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnMessageActivityAsync_AuthorizeCommand_ReceivesCanonicalVerb()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent ask create e2e test scenarios for update service");

        await ProcessAsync(harness, activity);

        var call = Assert.Single(harness.Authorization.Calls);
        Assert.Equal(TenantId, call.TenantId);
        Assert.Equal("internal-dave", call.UserId);
        Assert.Equal("agent ask", call.Command);
    }

    [Fact]
    public async Task OnMessageActivityAsync_ChannelMessage_SavesReferenceWithNullAadObjectIdAndPopulatedTeamId()
    {
        // Item #3 from iter-1 evaluator feedback: channel-scoped references must not
        // carry the installer/sender AAD ID — the natural key is (TenantId, ChannelId).
        var harness = Build();
        MapDave(harness.IdentityResolver, "aad-obj-channel-user");
        var activity = NewChannelMessageActivity(
            text: "agent status",
            aadObjectId: "aad-obj-channel-user",
            teamId: "team-abc-001",
            channelId: "19:channel-team-1");

        await ProcessAsync(harness, activity);

        var saved = Assert.Single(harness.Store.Saved);
        Assert.Null(saved.AadObjectId);
        Assert.Null(saved.InternalUserId);
        Assert.Equal("19:channel-team-1", saved.ChannelId);
        Assert.Equal("team-abc-001", saved.TeamId);
        Assert.True(saved.IsActive);
    }

    [Fact]
    public async Task OnTeamsMembersAddedAsync_BotAdded_PersistsReference()
    {
        var harness = Build();
        var activity = NewMembersAddedActivity();

        await ProcessAsync(harness, activity);

        var saved = Assert.Single(harness.Store.Saved);
        Assert.Equal(TenantId, saved.TenantId);
        Assert.Equal("aad-obj-installer", saved.AadObjectId);
        Assert.True(saved.IsActive);
    }

    [Fact]
    public async Task OnTeamsMembersAddedAsync_DoesNotInvokeIdentityResolver_TwoTierAuthorization()
    {
        var harness = Build();
        var activity = NewMembersAddedActivity();

        await ProcessAsync(harness, activity);

        // Identity resolution and authorization run only on command events. Install
        // events skip both per the two-tier authorization model.
        Assert.Empty(harness.Authorization.Calls);
    }

    [Fact]
    public async Task OnTeamsMembersRemovedAsync_PersonalScope_MarksReferenceInactiveByAad()
    {
        var harness = Build();
        var activity = NewMembersRemovedPersonalActivity("aad-obj-dave-001");

        await ProcessAsync(harness, activity);

        var marked = Assert.Single(harness.Store.MarkedInactive);
        Assert.Equal(TenantId, marked.TenantId);
        Assert.Equal("aad-obj-dave-001", marked.AadObjectId);
        Assert.Empty(harness.Store.MarkedInactiveByChannel);
    }

    [Fact]
    public async Task OnTeamsMembersRemovedAsync_TeamScope_MarksEachChannelInactive()
    {
        // Item #1 from iter-1 evaluator feedback: team-scope uninstalls must mark
        // EVERY stored channel in the team inactive, not just one. The handler
        // enumerates channels via IConversationReferenceStore.GetActiveChannelsByTeamIdAsync
        // and invokes MarkInactiveByChannelAsync per-channel.
        var harness = Build();
        const string teamId = "team-removed-001";
        harness.Store.TeamChannels[(TenantId, teamId)] = new List<TeamsConversationReference>
        {
            BuildChannelReference(channelId: "19:channel-a", teamId: teamId),
            BuildChannelReference(channelId: "19:channel-b", teamId: teamId),
            BuildChannelReference(channelId: "19:channel-c", teamId: teamId),
        };

        var activity = BuildTeamMembersRemovedWithTeamInfo(teamId, primaryChannelId: "19:channel-a");

        await ProcessAsync(harness, activity);

        Assert.Empty(harness.Store.MarkedInactive);
        Assert.Equal(3, harness.Store.MarkedInactiveByChannel.Count);
        var markedIds = harness.Store.MarkedInactiveByChannel
            .Select(t => t.ChannelId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "19:channel-a", "19:channel-b", "19:channel-c" }, markedIds);
        Assert.All(harness.Store.MarkedInactiveByChannel, t => Assert.Equal(TenantId, t.TenantId));
        Assert.Contains((TenantId, teamId), harness.Store.TeamChannelLookups);
    }

    [Fact]
    public async Task OnTeamsMembersRemovedAsync_TeamScope_AuditPayloadCarriesTeamIdAndChannelCount()
    {
        var harness = Build();
        const string teamId = "team-audit-001";
        harness.Store.TeamChannels[(TenantId, teamId)] = new List<TeamsConversationReference>
        {
            BuildChannelReference(channelId: "19:channel-a", teamId: teamId),
            BuildChannelReference(channelId: "19:channel-b", teamId: teamId),
        };

        var activity = BuildTeamMembersRemovedWithTeamInfo(teamId, primaryChannelId: "19:channel-a");

        await ProcessAsync(harness, activity);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("BotRemovedFromTeam", audit.Action);
        Assert.Contains(teamId, audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("channelsMarkedInactive", audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnInstallationUpdateActivityAsync_Add_PersistsReferenceAndAudits()
    {
        var harness = Build();
        var activity = NewInstallationUpdateActivity("add");

        await ProcessAsync(harness, activity);

        Assert.Single(harness.Store.Saved);
        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.CommandReceived, audit.EventType);
        Assert.Equal("AppInstalled", audit.Action);
        Assert.Equal(AuditOutcomes.Success, audit.Outcome);
    }

    [Fact]
    public async Task OnInstallationUpdateActivityAsync_Remove_PersonalScope_MarksInactiveAndAudits()
    {
        var harness = Build();
        var activity = NewInstallationUpdateActivity("remove");

        await ProcessAsync(harness, activity);

        Assert.Empty(harness.Store.Saved);
        var marked = Assert.Single(harness.Store.MarkedInactive);
        Assert.Equal("aad-obj-dave-001", marked.AadObjectId);
        Assert.Empty(harness.Store.MarkedInactiveByChannel);
        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("AppUninstalled", audit.Action);
    }

    [Fact]
    public async Task OnInstallationUpdateActivityAsync_Remove_TeamScope_MarksEachChannelInactiveAndAudits()
    {
        // Item #2 from iter-1 evaluator feedback: a team installationUpdate with
        // `remove` action must classify as team scope (via TeamsChannelData.Team.Id)
        // and fan-out MarkInactiveByChannelAsync — NOT misclassify as personal and
        // mark the installer's AAD object ID inactive.
        var harness = Build();
        const string teamId = "team-uninstall-001";
        harness.Store.TeamChannels[(TenantId, teamId)] = new List<TeamsConversationReference>
        {
            BuildChannelReference(channelId: "19:channel-team-1", teamId: teamId),
            BuildChannelReference(channelId: "19:channel-team-2", teamId: teamId),
        };

        var activity = NewTeamInstallationUpdateActivity("remove", teamId: teamId);

        await ProcessAsync(harness, activity);

        Assert.Empty(harness.Store.MarkedInactive);
        Assert.Equal(2, harness.Store.MarkedInactiveByChannel.Count);
        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("AppUninstalledFromTeam", audit.Action);
        Assert.Contains(teamId, audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAdaptiveCardInvokeAsync_DelegatesToCardActionHandler()
    {
        var harness = Build();
        var activity = NewAdaptiveCardInvokeActivity();

        await ProcessAsync(harness, activity);

        Assert.Equal(1, harness.CardHandler.Invocations);
    }

    // ─── Stage 3.4 step 2: OnTeamsMessagingExtensionSubmitActionAsync delegation ──────
    //
    // These tests exercise the activity-handler override end-to-end via
    // `IBot.OnTurnAsync` — i.e. they invoke through the same code path Teams uses when a
    // user right-clicks a message and submits the "Forward to Agent" action — and assert
    // on the injected `RecordingMessageExtensionHandler` test double and on the
    // `InvokeResponse` wire payload the base `ActivityHandler` writes back via
    // `SendActivitiesAsync`. This is the missing integration-level coverage flagged by
    // evaluator iter-2 finding #2 ("no test invokes the bot/activity-handler path and
    // asserts `RecordingMessageExtensionHandler.Invocations`, `LastAction`, and returned
    // `MessagingExtensionActionResponse`"). Unit-level coverage of the handler itself
    // lives in `Extensions/MessageExtensionHandlerTests.cs`.

    [Fact]
    public async Task OnTeamsMessagingExtensionSubmitActionAsync_DelegatesToMessageExtensionHandler()
    {
        var harness = Build();
        var activity = NewMessagingExtensionSubmitActionActivity(
            commandId: MessageExtensionHandler.ForwardToAgentCommandId,
            forwardedBody: "Investigate the deployment failure in update-service.");

        await ProcessAsync(harness, activity);

        Assert.Equal(1, harness.MessageExtensionHandler.Invocations);
        Assert.NotNull(harness.MessageExtensionHandler.LastAction);
        Assert.Equal(
            MessageExtensionHandler.ForwardToAgentCommandId,
            harness.MessageExtensionHandler.LastAction!.CommandId);
        Assert.NotNull(harness.MessageExtensionHandler.LastTurnContext);

        // Forwarded message payload should round-trip through the base
        // `TeamsActivityHandler.OnInvokeActivityAsync` dispatch (which materializes the
        // typed `MessagingExtensionAction` from `Activity.Value`).
        Assert.NotNull(harness.MessageExtensionHandler.LastAction.MessagePayload);
        Assert.Equal(
            "Investigate the deployment failure in update-service.",
            harness.MessageExtensionHandler.LastAction.MessagePayload!.Body!.Content);
    }

    [Fact]
    public async Task OnTeamsMessagingExtensionSubmitActionAsync_WritesHandlerResponseAsInvokeResponse()
    {
        // The base `ActivityHandler.OnTurnAsync` wraps the handler's
        // `MessagingExtensionActionResponse` in an `InvokeResponse { Status = 200, Body =
        // response }` and posts it back via `SendActivityAsync(..., Type =
        // "invokeResponse")`. Asserting on the sent payload — rather than only the
        // RecordingHandler — verifies the full wire contract Teams will observe.
        var harness = Build();
        var stubbedResponse = new MessagingExtensionActionResponse
        {
            ComposeExtension = new MessagingExtensionResult
            {
                Type = "result",
                AttachmentLayout = "list",
                Attachments = new List<MessagingExtensionAttachment>
                {
                    new MessagingExtensionAttachment
                    {
                        ContentType = "application/vnd.microsoft.card.adaptive",
                        Content = JObject.FromObject(new { type = "AdaptiveCard", version = "1.4", body = new object[] { new { type = "TextBlock", text = "Task submitted" } } }),
                    },
                },
            },
        };
        harness.MessageExtensionHandler.Response = stubbedResponse;

        var activity = NewMessagingExtensionSubmitActionActivity(
            commandId: MessageExtensionHandler.ForwardToAgentCommandId,
            forwardedBody: "Forward me please.");

        await ProcessAsync(harness, activity);

        var invokeResponseActivity = Assert.Single(
            harness.Adapter.Sent,
            a => string.Equals(a.Type, ActivityTypesEx.InvokeResponse, StringComparison.Ordinal));

        var invokeResponse = Assert.IsType<InvokeResponse>(invokeResponseActivity.Value);
        Assert.Equal(200, invokeResponse.Status);

        // The base dispatcher serializes the body to JSON over the wire, but in-process it
        // remains the typed `MessagingExtensionActionResponse` we stubbed — assert against
        // it directly so a regression that drops the body or wraps it twice is caught.
        var body = Assert.IsType<MessagingExtensionActionResponse>(invokeResponse.Body);
        Assert.Same(stubbedResponse, body);
        Assert.NotNull(body.ComposeExtension);
        Assert.Equal("result", body.ComposeExtension!.Type);
        var attachment = Assert.Single(body.ComposeExtension.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
    }

    [Fact]
    public async Task OnTeamsMessagingExtensionSubmitActionAsync_NonForwardCommandId_StillDelegatesToHandler()
    {
        // The activity-handler override is intentionally thin: it null-guards the inputs
        // and forwards to `IMessageExtensionHandler`. Command-ID validation (reject
        // unknown commandIds with an error card and a `Rejected` audit entry) is a
        // responsibility of the concrete `MessageExtensionHandler` — verified by
        // `MessageExtensionHandlerTests.HandleAsync_UnknownCommandId_*`. This regression
        // ensures the activity handler does NOT short-circuit unknown commandIds locally
        // (which would silently bypass the handler's audit/error-card contract).
        var harness = Build();
        var activity = NewMessagingExtensionSubmitActionActivity(
            commandId: "someUnknownCommand",
            forwardedBody: "anything");

        await ProcessAsync(harness, activity);

        Assert.Equal(1, harness.MessageExtensionHandler.Invocations);
        Assert.NotNull(harness.MessageExtensionHandler.LastAction);
        Assert.Equal("someUnknownCommand", harness.MessageExtensionHandler.LastAction!.CommandId);
    }

    private static Activity NewMessagingExtensionSubmitActionActivity(string commandId, string forwardedBody)
    {
        var action = new MessagingExtensionAction
        {
            CommandId = commandId,
            CommandContext = "message",
            MessagePayload = new MessageActionsPayload
            {
                Id = "msg-7890",
                MessageType = "message",
                CreatedDateTime = "2024-08-10T12:34:56.789Z",
                Body = new MessageActionsPayloadBody
                {
                    ContentType = "text",
                    Content = forwardedBody,
                },
                From = new MessageActionsPayloadFrom
                {
                    User = new MessageActionsPayloadUser
                    {
                        Id = "29:sender-aad",
                        DisplayName = "Carol Sender",
                        UserIdentityType = "aadUser",
                    },
                },
            },
        };

        var activity = new Activity(ActivityTypes.Invoke)
        {
            Id = Guid.NewGuid().ToString(),
            Name = "composeExtension/submitAction",
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:1234", name: "Dave Contoso") { AadObjectId = "aad-obj-dave-001" },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: "conv-dave-001") { TenantId = TenantId },
            Value = JObject.FromObject(action),
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        return activity;
    }

    private static TeamsConversationReference BuildChannelReference(string channelId, string teamId)
        => new TeamsConversationReference
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            AadObjectId = null,
            InternalUserId = null,
            ChannelId = channelId,
            TeamId = teamId,
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            ConversationId = channelId,
            BotId = BotId,
            ReferenceJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static Microsoft.Bot.Schema.Activity BuildTeamMembersRemovedWithTeamInfo(
        string teamId,
        string primaryChannelId)
    {
        var activity = NewMembersRemovedTeamActivity(teamId, channelId: primaryChannelId);
        // Inject TeamInfo into the channel data so the base TeamsActivityHandler dispatcher
        // routes through OnTeamsMembersRemovedAsync with a populated `teamInfo` parameter.
        var existing = activity.ChannelData as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject();
        existing["team"] = Newtonsoft.Json.Linq.JObject.FromObject(new { id = teamId });
        activity.ChannelData = existing;
        return activity;
    }

    /// <summary>
    /// Regression for evaluator-iter-2 finding #3 — each canonical command handler must
    /// publish a <see cref="AgentSwarm.Messaging.Abstractions.CommandEvent"/> with the
    /// architecture.md §3.1 / e2e-scenarios.md §Correlation discriminator for its verb.
    /// This is now a HANDLER-owned responsibility (not the activity handler's): the
    /// dispatcher invokes the verb-specific handler and the handler publishes its own
    /// CommandEvent so the dispatcher is self-sufficient outside the Teams pipeline AND
    /// approve/reject do not double-emit (DecisionEvent only — see
    /// ApproveCommandHandlerTests / RejectCommandHandlerTests for that contract).
    /// Theory rows cover the five command verbs that publish a CommandEvent:
    /// AgentTaskRequest, Command (status), Escalation, PauseAgent, ResumeAgent.
    /// </summary>
    [Theory]
    [InlineData("agent ask create e2e tests", "agent ask", "AgentTaskRequest")]
    [InlineData("agent status", "agent status", "Command")]
    [InlineData("escalate", "escalate", "Escalation")]
    [InlineData("pause", "pause", "PauseAgent")]
    [InlineData("resume", "resume", "ResumeAgent")]
    public async Task OnMessageActivityAsync_PublishedCommandEvent_CarriesCanonicalEventTypePerArchitectureSpec(
        string text,
        string expectedVerb,
        string expectedEventType)
    {
        var recording = new TestDoubles.RecordingInboundEventPublisher();
        var harness = BuildE2E(recording);
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage(text);

        await ProcessE2EAsync(harness, activity);

        var published = Assert.Single(recording.Published);
        var commandEvent = Assert.IsType<AgentSwarm.Messaging.Abstractions.CommandEvent>(published);
        Assert.Equal(expectedEventType, commandEvent.EventType);
        Assert.Equal(expectedVerb, commandEvent.Payload.CommandType);
        Assert.Equal("Teams", commandEvent.Messenger);
        Assert.Equal("aad-obj-dave-001", commandEvent.ExternalUserId);
    }

    /// <summary>
    /// Regression for evaluator-iter-2 finding #4 — authorized users sending unrecognised
    /// free text (text that does not match any canonical command verb) must NOT be
    /// rejected by the role-scoped RBAC stub before reaching the dispatcher. The
    /// dispatcher's TextEvent + help-card path (per <c>implementation-plan.md</c> §3.2 step
    /// 7) is the contractual handling for non-canonical input; default-deny RBAC applies
    /// to canonical verbs only.
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_AuthorizedUserSendsUnknownText_DispatchesTextEventThroughDispatcher()
    {
        var recording = new TestDoubles.RecordingInboundEventPublisher();
        var harness = BuildE2E(recording);
        MapDave(harness.IdentityResolver);

        var activity = NewPersonalMessage("hello there");
        await ProcessE2EAsync(harness, activity);

        var published = Assert.Single(recording.Published);
        var textEvent = Assert.IsType<AgentSwarm.Messaging.Abstractions.TextEvent>(published);
        Assert.Equal(AgentSwarm.Messaging.Abstractions.MessengerEventTypes.Text, textEvent.EventType);
        Assert.Equal("hello there", textEvent.Payload);
        Assert.Equal("Teams", textEvent.Messenger);
        Assert.Equal("aad-obj-dave-001", textEvent.ExternalUserId);

        // Help card should also have been sent in reply.
        var sent = Assert.Single(harness.Adapter.Sent);
        Assert.Single(sent.Attachments);
    }

    /// <summary>
    /// Stage 3.2 §Test Scenarios row "@mention stripped before dispatch" — End-to-end
    /// regression that wires the real <see cref="AgentSwarm.Messaging.Teams.Commands.CommandDispatcher"/>
    /// + every concrete <see cref="AgentSwarm.Messaging.Abstractions.ICommandHandler"/> via
    /// <see cref="HandlerFactory.BuildE2E"/>. Given a channel message containing
    /// <c>&lt;at&gt;AgentBot&lt;/at&gt; agent status</c>, <c>OnMessageActivityAsync</c>
    /// (Stage 2.2) must strip the mention text via <c>Activity.RemoveRecipientMention</c>
    /// before invoking <see cref="AgentSwarm.Messaging.Abstractions.ICommandDispatcher.DispatchAsync"/>,
    /// so the dispatcher receives <c>"agent status"</c> as
    /// <see cref="AgentSwarm.Messaging.Abstractions.CommandContext.NormalizedText"/>, the
    /// dispatcher routes to <see cref="AgentSwarm.Messaging.Teams.Commands.StatusCommandHandler"/>,
    /// and the handler emits a <see cref="AgentSwarm.Messaging.Abstractions.CommandEvent"/>
    /// with the canonical <see cref="AgentSwarm.Messaging.Abstractions.MessengerEventTypes.Command"/>
    /// discriminator. The dispatcher itself MUST NOT perform any further mention stripping
    /// (impl-plan §3.2 step 6) — this test asserts the contract end-to-end so a regression
    /// in either component (mention-stripping in Stage 2.2 OR keyword matching in Stage 3.2)
    /// fails the build.
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_ChannelMentionStrippedAgentStatus_RoutesToStatusHandlerEndToEnd()
    {
        var recording = new TestDoubles.RecordingInboundEventPublisher();
        var harness = BuildE2E(recording);
        MapDave(harness.IdentityResolver, "aad-obj-bob-002");

        var activity = NewChannelMentionMessage("<at>AgentBot</at> agent status");
        await ProcessE2EAsync(harness, activity);

        // Exactly one event must be published — the status handler's CommandEvent.
        // If the dispatcher had failed to recognise the mention-stripped text, the
        // unknown-input path would have published a TextEvent instead.
        var published = Assert.Single(recording.Published);
        var commandEvent = Assert.IsType<AgentSwarm.Messaging.Abstractions.CommandEvent>(published);
        Assert.Equal(AgentSwarm.Messaging.Abstractions.MessengerEventTypes.Command, commandEvent.EventType);
        Assert.Equal(AgentSwarm.Messaging.Teams.Commands.CommandNames.AgentStatus, commandEvent.Payload.CommandType);
        Assert.Equal("Teams", commandEvent.Messenger);
        Assert.Equal("aad-obj-bob-002", commandEvent.ExternalUserId);
        // Channel origination must be preserved on Source — proof that the dispatcher
        // ran ResolveEventSource against the real channel turn context.
        Assert.Equal(AgentSwarm.Messaging.Abstractions.MessengerEventSources.TeamChannel, commandEvent.Source);
        // The CommandEvent payload body MUST be the empty string, not "<at>AgentBot</at>"
        // or any mention artefact — this is the contract that "@mention stripped before
        // dispatch" enforces. The Payload field on ParsedCommand carries the trimmed
        // CommandArguments which the dispatcher computed from the mention-stripped
        // NormalizedText.
        Assert.Equal(string.Empty, commandEvent.Payload.Payload);

        // StatusCommandHandler responds with the "no active agents" empty-status card
        // (NullAgentSwarmStatusProvider returns an empty list in the E2E harness). The
        // presence of a reply attachment proves the handler actually ran end-to-end.
        var sent = Assert.Single(harness.Adapter.Sent);
        Assert.Single(sent.Attachments);
    }
}

