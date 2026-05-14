namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persistent store for <see cref="TeamsCardState"/> records — the mapping of
/// <c>AgentQuestion.QuestionId</c> to Teams activity ID required for card update / delete.
/// Aligned with <c>architecture.md</c> §4.3.
/// </summary>
/// <remarks>
/// Co-located with <see cref="TeamsCardState"/> in <c>AgentSwarm.Messaging.Teams</c> to
/// avoid a circular Abstractions → Teams assembly reference (the Teams record carries the
/// serialized Bot Framework <c>ConversationReference</c>). Stage 2.1 registers
/// <c>NoOpCardStateStore</c> as the default DI implementation; Stage 3.3 swaps in the
/// SQL-backed <c>SqlCardStateStore</c>.
/// </remarks>
public interface ICardStateStore
{
    /// <summary>
    /// Persist the supplied card state row (insert-or-replace keyed by
    /// <see cref="TeamsCardState.QuestionId"/>).
    /// </summary>
    /// <param name="state">The card state to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(TeamsCardState state, CancellationToken ct);

    /// <summary>
    /// Retrieve the full card state (including
    /// <see cref="TeamsCardState.ConversationReferenceJson"/>) for the supplied
    /// <c>AgentQuestion.QuestionId</c>. Returns <c>null</c> when no record exists.
    /// </summary>
    /// <param name="questionId">The question whose card state to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Update the <see cref="TeamsCardState.Status"/> field for the row identified by
    /// <paramref name="questionId"/>. Also stamps <see cref="TeamsCardState.UpdatedAt"/>
    /// with the current UTC time.
    /// </summary>
    /// <param name="questionId">The question whose card state should be updated.</param>
    /// <param name="newStatus">The new lifecycle status (<c>Pending</c>, <c>Answered</c>, <c>Expired</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct);
}
