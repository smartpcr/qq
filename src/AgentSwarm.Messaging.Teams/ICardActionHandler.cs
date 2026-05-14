using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Processes Adaptive Card <c>Action.Submit</c> invoke activities. Implementations extract
/// the action ID and optional comment from <c>turnContext.Activity.Value</c>, resolve the
/// originating <c>AgentQuestion</c>, validate the action against the question's
/// <c>AllowedActions</c>, and produce a <c>HumanDecisionEvent</c>.
/// </summary>
/// <remarks>
/// The interface lives in <c>AgentSwarm.Messaging.Teams</c> (rather than Abstractions)
/// because the signature depends on Bot Framework's <see cref="ITurnContext"/> and
/// <see cref="AdaptiveCardInvokeResponse"/> types. Stage 2.1 registers
/// <c>NoOpCardActionHandler</c> as the default DI implementation; Stage 3.3 introduces the
/// concrete <c>CardActionHandler</c>.
/// </remarks>
public interface ICardActionHandler
{
    /// <summary>
    /// Handle a single Adaptive Card invoke activity.
    /// </summary>
    /// <param name="turnContext">The Bot Framework turn context carrying the invoke activity.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>
    /// The response body to send back to Teams as the invoke result (containing either the
    /// updated card payload, a confirmation message, or an error explanation).
    /// </returns>
    Task<AdaptiveCardInvokeResponse> HandleAsync(ITurnContext turnContext, CancellationToken ct);
}
