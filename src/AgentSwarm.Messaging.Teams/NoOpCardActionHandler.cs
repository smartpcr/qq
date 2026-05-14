using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Stub <see cref="ICardActionHandler"/> registered in Stage 2.1 until the concrete
/// <c>CardActionHandler</c> lands in Stage 3.3. Returns an
/// <see cref="AdaptiveCardInvokeResponse"/> with a plain-text message explaining that card
/// actions are not yet wired up.
/// </summary>
public sealed class NoOpCardActionHandler : ICardActionHandler
{
    /// <inheritdoc />
    public Task<AdaptiveCardInvokeResponse> HandleAsync(ITurnContext turnContext, CancellationToken ct)
    {
        if (turnContext is null) throw new ArgumentNullException(nameof(turnContext));
        ct.ThrowIfCancellationRequested();

        var response = new AdaptiveCardInvokeResponse
        {
            StatusCode = 200,
            Type = "application/vnd.microsoft.activity.message",
            Value = "Adaptive Card actions are not yet available — the production CardActionHandler ships in Stage 3.3.",
        };

        return Task.FromResult(response);
    }
}
