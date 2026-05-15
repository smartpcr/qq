using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Helpers that build the handler under test and the Teams-shaped activities used by the
/// scenarios. Centralized here so individual test files focus on Arrange / Assert.
/// </summary>
internal static class HandlerFactory
{
    public const string TenantId = "contoso-tenant-id";
    public const string BotId = "bot-app-id";
    public const string BotName = "AgentBot";

    public sealed record Harness(
        TeamsSwarmActivityHandler Handler,
        RecordingConversationReferenceStore Store,
        RecordingCommandDispatcher Dispatcher,
        FakeIdentityResolver IdentityResolver,
        AlwaysAuthorizationService Authorization,
        RecordingAuditLogger AuditLogger,
        RecordingCardActionHandler CardHandler,
        IInboundEventPublisher EventPublisher,
        InertBotAdapter Adapter);

    public static Harness Build() => Build(new RecordingInboundEventPublisher());

    /// <summary>
    /// Build a handler harness whose inbound-event publisher is the supplied instance.
    /// Used by Stage 2.3 connector end-to-end tests to wire the activity handler's
    /// inbound publisher to the same <see cref="ChannelInboundEventPublisher"/> the
    /// connector reads from, so a real <c>OnMessageActivityAsync</c> -> publisher ->
    /// <c>TeamsMessengerConnector.ReceiveAsync</c> round-trip can be exercised without
    /// test-side shortcuts.
    /// </summary>
    public static Harness Build(IInboundEventPublisher eventPublisher)
    {
        if (eventPublisher is null) throw new ArgumentNullException(nameof(eventPublisher));

        var store = new RecordingConversationReferenceStore();
        var dispatcher = new RecordingCommandDispatcher();
        var identityResolver = new FakeIdentityResolver();
        var authorization = new AlwaysAuthorizationService();
        var auditLogger = new RecordingAuditLogger();
        var cardHandler = new RecordingCardActionHandler();
        var handler = new TeamsSwarmActivityHandler(
            store,
            dispatcher,
            identityResolver,
            authorization,
            new StubAgentQuestionStore(),
            auditLogger,
            cardHandler,
            eventPublisher,
            NullLogger<TeamsSwarmActivityHandler>.Instance);
        return new Harness(handler, store, dispatcher, identityResolver, authorization, auditLogger, cardHandler, eventPublisher, new InertBotAdapter());
    }

    public static UserIdentity MapDave(FakeIdentityResolver resolver, string aadObjectId = "aad-obj-dave-001")
    {
        var identity = new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: aadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator");
        resolver.Map(aadObjectId, identity);
        return identity;
    }

    public static Activity NewPersonalMessage(string text, string aadObjectId = "aad-obj-dave-001", string? correlationId = null)
    {
        var activity = new Activity(ActivityTypes.Message)
        {
            Id = Guid.NewGuid().ToString(),
            Text = text,
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:1234", name: "Dave Contoso") { AadObjectId = aadObjectId },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: "conv-dave-001") { TenantId = TenantId },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        if (correlationId is not null)
        {
            activity.Properties = activity.Properties ?? new JObject();
            activity.Properties["correlationId"] = correlationId;
        }

        return activity;
    }

    public static Activity NewChannelMentionMessage(string text, string aadObjectId = "aad-obj-bob-002")
    {
        var activity = new Activity(ActivityTypes.Message)
        {
            Id = Guid.NewGuid().ToString(),
            Text = text,
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:5678", name: "Bob Contoso") { AadObjectId = aadObjectId },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: "19:channel-conv-1") { TenantId = TenantId, ConversationType = "channel" },
            Entities = new List<Entity>
            {
                BuildMentionEntity(BotId, BotName, "<at>AgentBot</at>"),
            },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
            channel = new { id = "19:channel-conv-1" },
        });

        return activity;
    }

    public static Activity NewMembersAddedActivity()
    {
        var activity = new Activity(ActivityTypes.ConversationUpdate)
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:installer", name: "Installer") { AadObjectId = "aad-obj-installer" },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: "conv-install-001") { TenantId = TenantId },
            MembersAdded = new List<ChannelAccount>
            {
                new ChannelAccount(id: BotId, name: BotName),
            },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        return activity;
    }

    /// <summary>
    /// Build a team-scope members-added activity simulating a Teams install where the
    /// SDK populates <c>ChannelData.team.id</c> and the <c>TeamInfo</c> handler argument
    /// but <b>omits</b> <c>ChannelData.channel.id</c>. Used by the regression test for
    /// iter-1 evaluator feedback item #2 (team installs without channel ID were
    /// previously misclassified as personal references keyed by the installer AAD).
    /// </summary>
    /// <param name="teamId">Team ID surfaced in <c>ChannelData.team.id</c> and the
    /// dispatched <see cref="TeamInfo"/>.</param>
    /// <param name="conversationId">Conversation ID — defaults to a Teams channel-thread
    /// shape <c>19:xxx@thread.tacv2</c>, which is what the handler falls back to as the
    /// effective channel ID.</param>
    /// <param name="channelId">Optional explicit <c>ChannelData.channel.id</c>. When
    /// <c>null</c> (the default), the channel record is omitted entirely so the handler
    /// must rely on the team scope hint + conversation ID fallback.</param>
    public static Activity NewTeamMembersAddedActivity(
        string teamId,
        string conversationId = "19:team-conv-no-channel-id@thread.tacv2",
        string? channelId = null)
    {
        var activity = new Activity(ActivityTypes.ConversationUpdate)
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:team-installer", name: "Team Installer") { AadObjectId = "aad-obj-team-installer" },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: conversationId) { TenantId = TenantId, ConversationType = "channel" },
            MembersAdded = new List<ChannelAccount>
            {
                new ChannelAccount(id: BotId, name: BotName),
            },
        };

        // ChannelData carries tenant + team but NOT channel — simulating Teams payloads
        // that omit Channel.Id on team-level install events.
        if (channelId is null)
        {
            activity.ChannelData = JObject.FromObject(new
            {
                tenant = new { id = TenantId },
                team = new { id = teamId },
            });
        }
        else
        {
            activity.ChannelData = JObject.FromObject(new
            {
                tenant = new { id = TenantId },
                team = new { id = teamId },
                channel = new { id = channelId },
            });
        }

        return activity;
    }

    public static Activity NewMembersRemovedPersonalActivity(string aadObjectId)
    {
        var activity = new Activity(ActivityTypes.ConversationUpdate)
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:uninstaller", name: "Uninstaller") { AadObjectId = aadObjectId },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: "conv-personal-uninstall") { TenantId = TenantId },
            MembersRemoved = new List<ChannelAccount>
            {
                new ChannelAccount(id: BotId, name: BotName),
            },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        return activity;
    }

    public static Activity NewMembersRemovedTeamActivity(string teamId, string channelId = "19:channel-team-1")
    {
        var activity = new Activity(ActivityTypes.ConversationUpdate)
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:uninstaller", name: "Uninstaller") { AadObjectId = "aad-obj-uninstaller" },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: channelId) { TenantId = TenantId, ConversationType = "channel" },
            MembersRemoved = new List<ChannelAccount>
            {
                new ChannelAccount(id: BotId, name: BotName),
            },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
            team = new { id = teamId },
            channel = new { id = channelId },
        });

        return activity;
    }

    public static Activity NewChannelMessageActivity(string text, string aadObjectId, string teamId, string channelId)
    {
        var activity = new Activity(ActivityTypes.Message)
        {
            Id = Guid.NewGuid().ToString(),
            Text = text,
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:from-channel", name: "Channel User") { AadObjectId = aadObjectId },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: channelId) { TenantId = TenantId, ConversationType = "channel" },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
            team = new { id = teamId },
            channel = new { id = channelId },
        });

        return activity;
    }

    public static Activity NewInstallationUpdateActivity(string action)
    {
        var activity = new Activity(ActivityTypes.InstallationUpdate)
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            Action = action,
            From = new ChannelAccount(id: "29:1234", name: "Dave Contoso") { AadObjectId = "aad-obj-dave-001" },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: "conv-install-event") { TenantId = TenantId },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        return activity;
    }

    public static Activity NewTeamInstallationUpdateActivity(string action, string teamId, string channelId = "19:channel-team-1")
    {
        var activity = new Activity(ActivityTypes.InstallationUpdate)
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            Action = action,
            From = new ChannelAccount(id: "29:installer", name: "Team Installer") { AadObjectId = "aad-obj-team-installer" },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: channelId) { TenantId = TenantId, ConversationType = "channel" },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
            team = new { id = teamId },
            channel = new { id = channelId },
        });

        return activity;
    }

    public static Activity NewAdaptiveCardInvokeActivity()
    {
        var activity = new Activity(ActivityTypes.Invoke)
        {
            Id = Guid.NewGuid().ToString(),
            Name = "adaptiveCard/action",
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:1234", name: "Dave Contoso") { AadObjectId = "aad-obj-dave-001" },
            Recipient = new ChannelAccount(id: BotId, name: BotName),
            Conversation = new ConversationAccount(id: "conv-dave-001") { TenantId = TenantId },
            Value = JObject.FromObject(new
            {
                action = new
                {
                    type = "Action.Execute",
                    verb = "approve",
                    data = new { questionId = "Q-1001", actionId = "approve" },
                },
            }),
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        return activity;
    }

    public static async Task ProcessAsync(TeamsSwarmActivityHandler handler, Activity activity, CancellationToken ct = default)
    {
        var adapter = new InertBotAdapter();
        var turnContext = new TurnContext(adapter, activity);
        await ((IBot)handler).OnTurnAsync(turnContext, ct).ConfigureAwait(false);
    }

    public static async Task ProcessAsync(Harness harness, Activity activity, CancellationToken ct = default)
    {
        var turnContext = new TurnContext(harness.Adapter, activity);
        await ((IBot)harness.Handler).OnTurnAsync(turnContext, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Minimal <see cref="BotAdapter"/> that does not mutate the inbound activity. The
    /// in-box <c>TestAdapter</c> unconditionally overwrites <c>Recipient</c>,
    /// <c>Conversation</c>, and <c>ServiceUrl</c> from its internal
    /// <c>ConversationReference</c>, which clobbers the values our handler needs to
    /// observe (e.g. the AAD object ID encoded in <c>Recipient</c>, the conversation ID,
    /// and the mention markup in <c>Activity.Entities</c>). This adapter just records
    /// outbound replies and otherwise leaves the turn alone.
    /// </summary>
    public sealed class InertBotAdapter : BotAdapter
    {
        public List<Activity> Sent { get; } = new();

        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken cancellationToken)
        {
            Sent.AddRange(activities);
            var responses = new ResourceResponse[activities.Length];
            for (var i = 0; i < activities.Length; i++)
            {
                responses[i] = new ResourceResponse(id: Guid.NewGuid().ToString());
            }

            return Task.FromResult(responses);
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
            => Task.FromResult(new ResourceResponse(activity.Id ?? Guid.NewGuid().ToString()));

        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    public static Entity BuildMentionEntity(string id, string name, string mentionText)
    {
        var entity = new Entity { Type = "mention" };
        entity.SetAs(new Mention
        {
            Type = "mention",
            Text = mentionText,
            Mentioned = new ChannelAccount(id: id, name: name),
        });
        return entity;
    }
}
