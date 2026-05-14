namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// SOLE call path for Teams Adaptive Card update and delete operations. Aligned with
/// <c>architecture.md</c> §4.1.1.
/// </summary>
/// <remarks>
/// <para>
/// <c>TeamsMessengerConnector</c> (Stage 2.3) implements both this interface and
/// <see cref="AgentSwarm.Messaging.Abstractions.IMessengerConnector"/>; the orchestrator
/// resolves the Teams-aware connector through <see cref="ITeamsCardManager"/> when it needs
/// to mutate a previously sent card.
/// </para>
/// <para>
/// Both methods look up the persisted <see cref="TeamsCardState"/> for the supplied
/// question ID to find the Teams <c>ActivityId</c> and rehydrate the
/// <c>ConversationReference</c> required for the Bot Framework
/// <c>UpdateActivityAsync</c> / <c>DeleteActivityAsync</c> call.
/// </para>
/// </remarks>
public interface ITeamsCardManager
{
    /// <summary>
    /// Update the previously-sent Adaptive Card associated with <paramref name="questionId"/>
    /// to reflect the supplied <paramref name="action"/>.
    /// </summary>
    /// <param name="questionId">The originating <c>AgentQuestion.QuestionId</c> whose card should be updated.</param>
    /// <param name="action">The card update action to apply (answered / expired / cancelled).</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct);

    /// <summary>
    /// Delete the previously-sent Adaptive Card associated with <paramref name="questionId"/>.
    /// </summary>
    /// <param name="questionId">The originating <c>AgentQuestion.QuestionId</c> whose card should be removed.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    Task DeleteCardAsync(string questionId, CancellationToken ct);
}
