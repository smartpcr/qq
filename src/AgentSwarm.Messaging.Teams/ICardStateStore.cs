namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persistence contract for <see cref="TeamsCardState"/> records. Allows
/// <c>TeamsMessengerConnector</c> (Stage 2.3) to record the <c>activityId</c> returned from
/// a proactive card send so that <c>ITeamsCardManager.UpdateCardAsync</c> /
/// <c>DeleteCardAsync</c> (Stage 3.3) can later locate the card.
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>AgentSwarm.Messaging.Teams</c> (not Abstractions) because the contract
/// surface uses <see cref="TeamsCardState"/>, which is a Teams-specific type. This is the
/// canonical assembly assignment per <c>implementation-plan.md</c> §2.1 step 3 and the
/// architecture §7 assembly table.
/// </para>
/// <para>
/// The <c>NoOpCardStateStore</c> stub registered in Stage 2.1 keeps card state in memory so
/// the broader DI graph can be exercised before <c>SqlCardStateStore</c> ships in Stage 3.3.
/// </para>
/// </remarks>
public interface ICardStateStore
{
    /// <summary>Persist the supplied <paramref name="state"/>.</summary>
    /// <param name="state">The card state to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(TeamsCardState state, CancellationToken ct);

    /// <summary>Look up the card state for an <c>AgentQuestion</c>. Returns <c>null</c> when none exists.</summary>
    /// <param name="questionId">The originating <c>AgentQuestion.QuestionId</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="TeamsCardState"/>, or <c>null</c>.</returns>
    Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Transition the card's <see cref="TeamsCardState.Status"/> to
    /// <paramref name="newStatus"/>. Implementations also update
    /// <see cref="TeamsCardState.UpdatedAt"/>.
    /// </summary>
    /// <param name="questionId">The originating <c>AgentQuestion.QuestionId</c>.</param>
    /// <param name="newStatus">The new lifecycle status — should be one of <see cref="TeamsCardStatuses.All"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct);
}
