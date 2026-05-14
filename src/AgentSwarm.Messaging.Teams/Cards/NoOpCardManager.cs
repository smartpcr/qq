using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Stage-2.1 stub for <see cref="ITeamsCardManager"/>. Logs the call and returns; replaced by
/// the concrete card-managing <c>TeamsMessengerConnector</c> in Stage 3.3.
/// </summary>
public sealed class NoOpCardManager : ITeamsCardManager
{
    private readonly ILogger<NoOpCardManager> _logger;

    /// <summary>Initialize a new <see cref="NoOpCardManager"/>.</summary>
    public NoOpCardManager(ILogger<NoOpCardManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "NoOpCardManager.UpdateCardAsync called for question {QuestionId} action {Action}. Replace with concrete TeamsMessengerConnector in Stage 3.3.",
            questionId,
            action);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteCardAsync(string questionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "NoOpCardManager.DeleteCardAsync called for question {QuestionId}. Replace with concrete TeamsMessengerConnector in Stage 3.3.",
            questionId);
        return Task.CompletedTask;
    }
}
