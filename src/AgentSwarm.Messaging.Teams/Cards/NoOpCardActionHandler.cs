using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Stage-2.1 stub for <see cref="ICardActionHandler"/>. Returns a generic
/// "card actions not yet available" response so the activity-handler pipeline can be wired
/// before the concrete <c>CardActionHandler</c> lands in Stage 3.3.
/// </summary>
public sealed class NoOpCardActionHandler : ICardActionHandler
{
    private readonly ILogger<NoOpCardActionHandler> _logger;

    /// <summary>Initialize a new <see cref="NoOpCardActionHandler"/>.</summary>
    public NoOpCardActionHandler(ILogger<NoOpCardActionHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<AdaptiveCardInvokeResponse> HandleAsync(ITurnContext turnContext, CancellationToken ct)
    {
        _logger.LogInformation(
            "NoOpCardActionHandler invoked — card actions are not wired yet. Replace with concrete CardActionHandler in Stage 3.3.");

        var response = new AdaptiveCardInvokeResponse
        {
            StatusCode = 200,
            Type = "application/vnd.microsoft.activity.message",
            Value = "Card actions are not yet available.",
        };
        return Task.FromResult(response);
    }
}
