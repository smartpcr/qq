namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Sole call path for card update and delete operations driven by Adaptive Card lifecycle
/// events (answered, expired, cancelled). Aligned with <c>architecture.md</c> §4.1.1. The
/// concrete implementation is <c>TeamsMessengerConnector</c> (Stage 3.3), which also
/// implements <c>IMessengerConnector</c> so the same component owns send + manage paths.
/// </summary>
public interface ITeamsCardManager
{
    /// <summary>Update the rendered card associated with <paramref name="questionId"/> to
    /// reflect <paramref name="action"/> (answered / expired / cancelled).</summary>
    Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct);

    /// <summary>Delete the card associated with <paramref name="questionId"/>.</summary>
    Task DeleteCardAsync(string questionId, CancellationToken ct);
}
