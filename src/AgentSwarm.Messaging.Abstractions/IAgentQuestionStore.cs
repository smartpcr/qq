namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Persistence contract for <see cref="AgentQuestion"/> records. Aligned with
/// <c>architecture.md</c> §4.11. The concrete SQL-backed implementation
/// (<c>SqlAgentQuestionStore</c>) is provided by Stage 3.3.
/// </summary>
/// <remarks>
/// Status transitions follow a strict compare-and-set protocol via
/// <see cref="TryUpdateStatusAsync"/> to support first-writer-wins idempotency when two
/// concurrent pods race to record the same human decision
/// (see <c>architecture.md</c> §6.3).
/// </remarks>
public interface IAgentQuestionStore
{
    /// <summary>
    /// Persist a new <see cref="AgentQuestion"/> with <c>Status = "Open"</c>. Called by
    /// <c>TeamsMessengerConnector.SendQuestionAsync</c> before card rendering so that
    /// <c>CardActionHandler</c> can later resolve the question by ID.
    /// </summary>
    /// <param name="question">The question to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the row has been persisted.</returns>
    Task SaveAsync(AgentQuestion question, CancellationToken ct);

    /// <summary>
    /// Retrieve the full <see cref="AgentQuestion"/> by primary key (including the
    /// <see cref="AgentQuestion.AllowedActions"/> list and current
    /// <see cref="AgentQuestion.Status"/>). Returns <c>null</c> if the question is not found.
    /// </summary>
    /// <param name="questionId">The <see cref="AgentQuestion.QuestionId"/> to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The question, or <c>null</c> when no row exists.</returns>
    Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Atomically transition the question's <see cref="AgentQuestion.Status"/> from
    /// <paramref name="expectedStatus"/> to <paramref name="newStatus"/> using
    /// compare-and-set semantics. Returns <c>true</c> when the transition succeeded and
    /// <c>false</c> when the current status did not match <paramref name="expectedStatus"/>
    /// (first-writer-wins per <c>architecture.md</c> §6.3).
    /// </summary>
    /// <param name="questionId">The question to update.</param>
    /// <param name="expectedStatus">The status the row must currently hold for the update to succeed.</param>
    /// <param name="newStatus">The status to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> on success; <c>false</c> when the current status did not match.</returns>
    Task<bool> TryUpdateStatusAsync(
        string questionId,
        string expectedStatus,
        string newStatus,
        CancellationToken ct);

    /// <summary>
    /// Set the <see cref="AgentQuestion.ConversationId"/> field on a previously saved
    /// question. Called by <c>SendQuestionAsync</c> (Stage 2.3) and
    /// <c>TeamsProactiveNotifier</c> (Stage 4.2) once the proactive send completes and the
    /// Teams conversation ID is known.
    /// </summary>
    /// <param name="questionId">The question to update.</param>
    /// <param name="conversationId">The Teams conversation ID where the card was delivered.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the row has been updated.</returns>
    Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct);

    /// <summary>
    /// Return the most recently created question with <c>Status = "Open"</c> and a matching
    /// <see cref="AgentQuestion.ConversationId"/>, ordered by
    /// <see cref="AgentQuestion.CreatedAt"/> descending. Used for single-target bare
    /// <c>approve</c>/<c>reject</c> resolution per <c>architecture.md</c> §2.5.
    /// </summary>
    /// <param name="conversationId">The Teams conversation ID to filter on.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching question, or <c>null</c> when no open question exists for the conversation.</returns>
    Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct);

    /// <summary>
    /// Return ALL open questions with a matching <see cref="AgentQuestion.ConversationId"/>,
    /// ordered by <see cref="AgentQuestion.CreatedAt"/> descending. Used by
    /// <c>ApproveCommandHandler</c> and <c>RejectCommandHandler</c> (Stage 3.2) for bare
    /// <c>approve</c>/<c>reject</c> disambiguation.
    /// </summary>
    /// <param name="conversationId">The Teams conversation ID to filter on.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An ordered list (possibly empty) of open questions for the conversation.</returns>
    Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct);

    /// <summary>
    /// Return up to <paramref name="batchSize"/> questions where <c>Status = "Open"</c> and
    /// <see cref="AgentQuestion.ExpiresAt"/> &lt; <paramref name="cutoff"/>. Used by
    /// <c>QuestionExpiryProcessor</c> (Stage 3.3) to batch-scan questions whose deadline has
    /// elapsed.
    /// </summary>
    /// <param name="cutoff">The UTC instant; questions with <c>ExpiresAt</c> strictly less than this value are returned.</param>
    /// <param name="batchSize">Maximum number of questions to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An ordered list (possibly empty) of expired-but-still-open questions.</returns>
    Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct);
}
