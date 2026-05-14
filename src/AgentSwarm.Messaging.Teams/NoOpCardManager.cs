using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// No-op stub for <see cref="ITeamsCardManager"/> registered in Stage 2.1. Logs and returns
/// without invoking Bot Framework. Replaced in Stage 3.3 by the concrete
/// <c>TeamsMessengerConnector</c> implementation (which itself implements
/// <see cref="ITeamsCardManager"/> per <c>architecture.md</c> §4.1.1).
/// </summary>
public sealed class NoOpCardManager : ITeamsCardManager
{
    private readonly ILogger<NoOpCardManager> _logger;

    /// <summary>
    /// Initialize a new <see cref="NoOpCardManager"/>.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public NoOpCardManager(ILogger<NoOpCardManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "NoOpCardManager.UpdateCardAsync called for QuestionId={QuestionId} Action={Action}. " +
            "Replace with TeamsMessengerConnector (Stage 3.3) for live behavior.",
            questionId,
            action);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteCardAsync(string questionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "NoOpCardManager.DeleteCardAsync called for QuestionId={QuestionId}. " +
            "Replace with TeamsMessengerConnector (Stage 3.3) for live behavior.",
            questionId);
        return Task.CompletedTask;
    }
}
