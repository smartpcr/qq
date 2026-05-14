using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Adaptive Card invoke handler implemented by the Teams connector (Stage 3.3). Returns a
/// fully populated <see cref="AdaptiveCardInvokeResponse"/> so the Bot Framework adapter can
/// respond to the originating <c>adaptiveCard/action</c> invoke. The contract lives in
/// <c>AgentSwarm.Messaging.Teams</c> (not Abstractions) because the return type is
/// Bot-Framework-specific.
/// </summary>
public interface ICardActionHandler
{
    /// <summary>Process a single Adaptive Card action invoke and produce the response payload.</summary>
    /// <param name="turnContext">Inbound turn context carrying the invoke activity.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>The card invoke response surfaced to Teams.</returns>
    Task<AdaptiveCardInvokeResponse> HandleAsync(ITurnContext turnContext, CancellationToken ct);
}
