using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Extensions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

/// <summary>
/// Regression tests for iter-2 evaluator finding #2 — the RBAC identity-key mismatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>What broke:</b> Before iter-2, <see cref="TeamsSwarmActivityHandler"/> and
/// <see cref="MessageExtensionHandler"/> passed
/// <see cref="UserIdentity.InternalUserId"/> to
/// <see cref="IUserAuthorizationService.AuthorizeAsync"/>, but
/// <see cref="AgentSwarm.Messaging.Teams.Security.RbacAuthorizationService"/> /
/// <see cref="AgentSwarm.Messaging.Teams.Security.RbacOptions.TenantRoleAssignments"/>
/// are KEYED BY the Entra AAD object ID. Real RBAC config keyed by AAD would never
/// match an internal user ID, so every authorized user was silently denied.
/// </para>
/// <para>
/// <b>The contract going forward:</b> the only identifier the handlers may pass to
/// <c>AuthorizeAsync</c> is <see cref="UserIdentity.AadObjectId"/> (or the activity's
/// <c>From.AadObjectId</c> fallback). These tests deliberately use a
/// <see cref="UserIdentity"/> where <c>InternalUserId != AadObjectId</c> so a
/// regression that silently swaps the two would change the captured call argument and
/// fail the assertion. The default-pass <see cref="AlwaysAuthorizationService"/> stub
/// records the captured <c>userId</c> argument so the test asserts the exact value the
/// handler passed.
/// </para>
/// </remarks>
public sealed class RbacAadIdentityRegressionTests
{
    private const string TenantId = HandlerFactory.TenantId;
    private const string AadObjectId = "aad-obj-graph-real-001";
    private const string InternalUserId = "platform-internal-dave-42";

    [Fact]
    public async Task TeamsSwarmActivityHandler_PassesAadObjectId_NotInternalUserId_ToAuthorizationService()
    {
        var harness = HandlerFactory.Build();
        // Map a UserIdentity whose internal and AAD IDs INTENTIONALLY DIFFER so a
        // regression that swaps the two would surface as a captured-arg mismatch.
        harness.IdentityResolver.Map(AadObjectId, new UserIdentity(
            InternalUserId: InternalUserId,
            AadObjectId: AadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));

        var activity = NewMessageWithAadObjectId("agent ask plan migration", AadObjectId);

        await HandlerFactory.ProcessAsync(harness, activity);

        // Single call captured (rejects double-dispatch regressions) and the userId
        // arg MUST be the AAD object ID — the value RbacOptions matches on.
        var call = Assert.Single(harness.Authorization.Calls);
        Assert.Equal(TenantId, call.TenantId);
        Assert.Equal(AadObjectId, call.UserId);
        Assert.NotEqual(InternalUserId, call.UserId);
        Assert.Equal("agent ask", call.Command);
    }

    [Fact]
    public async Task MessageExtensionHandler_PassesAadObjectId_NotInternalUserId_ToAuthorizationService()
    {
        var identityResolver = new FakeIdentityResolver();
        var authorization = new AlwaysAuthorizationService();
        var auditLogger = new RecordingAuditLogger();
        var dispatcher = new RecordingCommandDispatcher();
        var handler = new MessageExtensionHandler(
            dispatcher,
            identityResolver,
            authorization,
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        identityResolver.Map(AadObjectId, new UserIdentity(
            InternalUserId: InternalUserId,
            AadObjectId: AadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));

        var (turnContext, action) = NewMessageExtensionTurnContext(AadObjectId, body: "Forward me please.");

        await handler.HandleAsync(turnContext, action, CancellationToken.None);

        var call = Assert.Single(authorization.Calls);
        Assert.Equal(TenantId, call.TenantId);
        Assert.Equal(AadObjectId, call.UserId);
        Assert.NotEqual(InternalUserId, call.UserId);
        Assert.Equal("agent ask", call.Command);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────

    private static Activity NewMessageWithAadObjectId(string text, string aadObjectId)
    {
        var activity = new Activity(ActivityTypes.Message)
        {
            Id = Guid.NewGuid().ToString(),
            Text = text,
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:" + aadObjectId, name: "Dave Contoso") { AadObjectId = aadObjectId },
            Recipient = new ChannelAccount(id: HandlerFactory.BotId, name: HandlerFactory.BotName),
            Conversation = new ConversationAccount(id: "conv-regression-1") { TenantId = TenantId },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        return activity;
    }

    private static (ITurnContext<IInvokeActivity> TurnContext, Microsoft.Bot.Schema.Teams.MessagingExtensionAction Action) NewMessageExtensionTurnContext(string aadObjectId, string body)
    {
        var action = new Microsoft.Bot.Schema.Teams.MessagingExtensionAction
        {
            CommandId = MessageExtensionHandler.ForwardToAgentCommandId,
            CommandContext = "message",
            MessagePayload = new Microsoft.Bot.Schema.Teams.MessageActionsPayload
            {
                Id = "msg-7890",
                MessageType = "message",
                CreatedDateTime = "2024-08-10T12:34:56.789Z",
                Body = new Microsoft.Bot.Schema.Teams.MessageActionsPayloadBody
                {
                    ContentType = "text",
                    Content = body,
                },
            },
        };

        var activity = new Activity(ActivityTypes.Invoke)
        {
            Id = Guid.NewGuid().ToString(),
            Name = "composeExtension/submitAction",
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:" + aadObjectId, name: "Dave Contoso") { AadObjectId = aadObjectId },
            Recipient = new ChannelAccount(id: HandlerFactory.BotId, name: HandlerFactory.BotName),
            Conversation = new ConversationAccount(id: "conv-regression-2") { TenantId = TenantId },
            Value = JObject.FromObject(action),
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        return (new InvokeTurnContextWrapper(new TurnContext(new HandlerFactory.InertBotAdapter(), activity)), action);
    }

    /// <summary>
    /// Local <see cref="ITurnContext{IInvokeActivity}"/> wrapper — the production
    /// <see cref="TurnContext"/> only implements the non-generic <see cref="ITurnContext"/>,
    /// so message-extension handler tests need a thin shim that re-exposes the activity as
    /// <see cref="IInvokeActivity"/>. Mirrors the private wrapper used by
    /// <c>MessageExtensionHandlerTests</c>.
    /// </summary>
    private sealed class InvokeTurnContextWrapper : ITurnContext<IInvokeActivity>
    {
        private readonly ITurnContext _inner;

        public InvokeTurnContextWrapper(ITurnContext inner) => _inner = inner;

        public IInvokeActivity Activity => (IInvokeActivity)_inner.Activity;

        Activity ITurnContext.Activity => _inner.Activity;

        public BotAdapter Adapter => _inner.Adapter;

        public TurnContextStateCollection TurnState => _inner.TurnState;

        public bool Responded => _inner.Responded;

        public ITurnContext OnDeleteActivity(DeleteActivityHandler handler) => _inner.OnDeleteActivity(handler);

        public ITurnContext OnSendActivities(SendActivitiesHandler handler) => _inner.OnSendActivities(handler);

        public ITurnContext OnUpdateActivity(UpdateActivityHandler handler) => _inner.OnUpdateActivity(handler);

        public Task<ResourceResponse> SendActivityAsync(string textReplyToSend, string? speak = null, string inputHint = "acceptingInput", CancellationToken cancellationToken = default)
            => _inner.SendActivityAsync(textReplyToSend, speak, inputHint, cancellationToken);

        public Task<ResourceResponse> SendActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => _inner.SendActivityAsync(activity, cancellationToken);

        public Task<ResourceResponse[]> SendActivitiesAsync(IActivity[] activities, CancellationToken cancellationToken = default)
            => _inner.SendActivitiesAsync(activities, cancellationToken);

        public Task<ResourceResponse> UpdateActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => _inner.UpdateActivityAsync(activity, cancellationToken);

        public Task DeleteActivityAsync(string activityId, CancellationToken cancellationToken = default)
            => _inner.DeleteActivityAsync(activityId, cancellationToken);

        public Task DeleteActivityAsync(ConversationReference conversationReference, CancellationToken cancellationToken = default)
            => _inner.DeleteActivityAsync(conversationReference, cancellationToken);
    }
}
