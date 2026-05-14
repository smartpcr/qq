namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persistence contract for <see cref="TeamsCardState"/>. Co-located with the record in
/// <c>AgentSwarm.Messaging.Teams</c> (not Abstractions) to keep the Abstractions assembly free
/// of any platform-specific type dependency — the SQL implementation lives in this assembly
/// as <c>SqlCardStateStore</c> (Stage 3.3). Aligned with <c>architecture.md</c> §4.3 and §7.
/// </summary>
public interface ICardStateStore
{
    /// <summary>Persist a new card-state row.</summary>
    Task SaveAsync(TeamsCardState state, CancellationToken ct);

    /// <summary>Return the full <see cref="TeamsCardState"/> for the supplied question, or
    /// null when no card has been recorded. The returned record includes
    /// <see cref="TeamsCardState.ConversationReferenceJson"/> so callers (Stage 4.2 expiry
    /// flow) can rehydrate the conversation without a second lookup.</summary>
    Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct);

    /// <summary>Update only the <see cref="TeamsCardState.Status"/> column. The
    /// implementation is expected to also bump <see cref="TeamsCardState.UpdatedAt"/>.</summary>
    Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct);
}
