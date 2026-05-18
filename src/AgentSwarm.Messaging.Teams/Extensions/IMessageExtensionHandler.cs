using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;

namespace AgentSwarm.Messaging.Teams.Extensions;

/// <summary>
/// Stage 3.4 handler for Teams message-extension action commands
/// (<c>composeExtension/submitAction</c>). Triggered by
/// <see cref="TeamsSwarmActivityHandler.OnTeamsMessagingExtensionSubmitActionAsync"/>
/// when a user invokes a message action (for example, right-clicks a message and selects
/// "Forward to Agent").
/// </summary>
/// <remarks>
/// <para>
/// Defined in the <c>AgentSwarm.Messaging.Teams</c> assembly (not Abstractions) because the
/// contract takes <see cref="ITurnContext{IInvokeActivity}"/> and returns
/// <see cref="MessagingExtensionActionResponse"/> — both Bot Framework Teams types. The
/// Abstractions assembly stays platform-agnostic, mirroring the
/// <see cref="ICardActionHandler"/> placement decision.
/// </para>
/// <para>
/// Concrete implementations extract the canonical message-payload triple required by the
/// Stage 3.4 work-item spec (body text, sender display name, source timestamp) and SHOULD
/// also capture the channel-post forensic context (subject / locale / link-to-message /
/// sender AAD ID) when present so the audit trail can identify the originating thread
/// even after the platform IDs rotate. See
/// <see cref="MessageExtensionHandler.ExtractMessagePayload"/> for the canonical
/// implementation.
/// </para>
/// </remarks>
public interface IMessageExtensionHandler
{
    /// <summary>
    /// Handle a single <c>composeExtension/submitAction</c> invoke.
    /// Implementations:
    /// <list type="number">
    /// <item><description>extract the forwarded message text, sender, and timestamp from
    /// <see cref="MessagingExtensionAction.MessagePayload"/>;</description></item>
    /// <item><description>delegate the extracted text to
    /// <see cref="AgentSwarm.Messaging.Abstractions.ICommandDispatcher.DispatchAsync"/>
    /// to publish a <see cref="AgentSwarm.Messaging.Abstractions.CommandEvent"/> of type
    /// <see cref="AgentSwarm.Messaging.Abstractions.MessengerEventTypes.AgentTaskRequest"/>
    /// with <see cref="AgentSwarm.Messaging.Abstractions.MessengerEventSources.MessageAction"/>;</description></item>
    /// <item><description>log a
    /// <see cref="AgentSwarm.Messaging.Persistence.AuditEventTypes.MessageActionReceived"/>
    /// audit entry;</description></item>
    /// <item><description>return a task-submitted confirmation card to the invoking user
    /// via the <see cref="MessagingExtensionActionResponse"/> invoke reply.</description></item>
    /// </list>
    /// </summary>
    /// <param name="turnContext">The invoke-activity turn context.</param>
    /// <param name="action">The submit-action payload deserialized by the base
    /// <see cref="Microsoft.Bot.Builder.Teams.TeamsActivityHandler"/>.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>The response delivered to Teams in the invoke ack.</returns>
    Task<MessagingExtensionActionResponse> HandleAsync(
        ITurnContext<IInvokeActivity> turnContext,
        MessagingExtensionAction action,
        CancellationToken ct);
}
