using Microsoft.Bot.Builder.Teams;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Microsoft Teams activity handler — entry point for all inbound Bot Framework activities
/// after middleware processing. Extends Bot Framework SDK <see cref="TeamsActivityHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.1 introduces this class as a minimal scaffold so the host can register it as
/// <c>IBot</c>. Stage 2.2 adds the constructor-injected dependencies
/// (<c>IConversationReferenceStore</c>, <c>ICommandDispatcher</c>, <c>IIdentityResolver</c>,
/// <c>IUserAuthorizationService</c>, <c>IAgentQuestionStore</c>, <c>IAuditLogger</c>,
/// <c>ICardActionHandler</c>, <c>IInboundEventPublisher</c>,
/// <c>ILogger&lt;TeamsSwarmActivityHandler&gt;</c>) and overrides for
/// <c>OnMessageActivityAsync</c>, <see cref="TeamsActivityHandler.OnTeamsMembersAddedAsync"/>,
/// <see cref="TeamsActivityHandler.OnTeamsMembersRemovedAsync"/>,
/// <c>OnInstallationUpdateActivityAsync</c>, <c>OnAdaptiveCardInvokeAsync</c>, and the
/// correlation-aware <c>OnTurnAsync</c> behaviors.
/// </para>
/// <para>
/// This stage's implementation keeps the default <see cref="TeamsActivityHandler"/>
/// behavior — every activity is acknowledged with HTTP 200 and no application-level
/// processing occurs. Tests at this stage assert that <c>/api/messages</c> accepts a valid
/// Bot Framework activity and returns HTTP 200, which the SDK default behavior already
/// satisfies.
/// </para>
/// </remarks>
public class TeamsSwarmActivityHandler : TeamsActivityHandler
{
    // Stage 2.1 placeholder. Stage 2.2 fills in the constructor dependencies and overrides.
}
