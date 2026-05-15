using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Processes Adaptive Card <c>Action.Submit</c> invoke activities raised by Teams when a
/// human taps a button on a card the bot previously sent. Defined in the
/// <c>AgentSwarm.Messaging.Teams</c> assembly (not Abstractions) because the contract
/// returns a Bot Framework <see cref="AdaptiveCardInvokeResponse"/> — the Abstractions
/// assembly is platform-agnostic and must not take a compile-time dependency on
/// <c>Microsoft.Bot.Builder</c>.
/// </summary>
/// <remarks>
/// The interface lives here per <c>implementation-plan.md</c> §2.1. The
/// <c>NoOpCardActionHandler</c> stub (registered in Stage 2.1 DI) returns a "card actions
/// not yet available" response so <see cref="TeamsSwarmActivityHandler"/> can be
/// constructed and exercised before the concrete <c>CardActionHandler</c> ships in Stage 3.3.
/// </remarks>
public interface ICardActionHandler
{
    /// <summary>
    /// Handle a single Adaptive Card invoke. Implementations extract <c>actionId</c> /
    /// optional <c>comment</c> from <see cref="Activity.Value"/>, resolve the originating
    /// <c>AgentQuestion</c> via <c>IAgentQuestionStore.GetByIdAsync</c>, validate the
    /// action against <c>AgentQuestion.AllowedActions</c>, emit a
    /// <c>HumanDecisionEvent</c>, and return a response card the Bot Framework displays
    /// to the user.
    /// </summary>
    /// <param name="turnContext">The invoke-activity turn context.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>The response delivered to Teams in the invoke ack.</returns>
    Task<AdaptiveCardInvokeResponse> HandleAsync(ITurnContext turnContext, CancellationToken ct);
}
