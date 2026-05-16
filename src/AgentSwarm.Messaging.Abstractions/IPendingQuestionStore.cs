namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Cross-platform store for in-flight agent questions. Tracks each question
/// from initial post through operator resolution (or timeout) so the connector
/// can route inbound interactions back to the correct question, capture the
/// chosen action, and emit timeout events for unresolved entries. See
/// architecture.md Section 4.7.
/// </summary>
/// <remarks>
/// Identifier types mirror the persistence schema: connector-native ids are
/// passed in as <see cref="long"/> (Discord snowflakes cast to <see cref="long"/>;
/// platforms with non-numeric ids hash deterministically to <see cref="long"/>
/// at the connector boundary).
/// </remarks>
public interface IPendingQuestionStore
{
    /// <summary>
    /// Persists the wrapped <paramref name="envelope"/> together with the
    /// platform-native routing identifiers returned from the initial post.
    /// </summary>
    /// <param name="envelope">The question envelope with routing metadata.</param>
    /// <param name="channelId">Platform-native channel identifier.</param>
    /// <param name="platformMessageId">Platform-native id of the posted message.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreAsync(
        AgentQuestionEnvelope envelope,
        long channelId,
        long platformMessageId,
        CancellationToken ct);

    /// <summary>
    /// Looks up a tracked question by its id. Returns <see langword="null"/>
    /// when no row exists (or when the entry has been pruned).
    /// </summary>
    /// <param name="questionId">Question id (matches <see cref="AgentQuestion.QuestionId"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Marks a question as <see cref="PendingQuestionStatus.Answered"/>.
    /// Idempotent in the answered terminal state.
    /// </summary>
    /// <param name="questionId">Question id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkAnsweredAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Marks a question as <see cref="PendingQuestionStatus.AwaitingComment"/>
    /// after the operator picks a <see cref="HumanAction"/> whose
    /// <see cref="HumanAction.RequiresComment"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="questionId">Question id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Records the operator's selection. Populates
    /// <see cref="PendingQuestion.SelectedActionId"/>,
    /// <see cref="PendingQuestion.SelectedActionValue"/>, and
    /// <see cref="PendingQuestion.RespondentUserId"/>. Does not by itself
    /// transition <see cref="PendingQuestion.Status"/> — the caller composes
    /// this with <see cref="MarkAnsweredAsync"/> or
    /// <see cref="MarkAwaitingCommentAsync"/> depending on whether a comment
    /// is required.
    /// </summary>
    /// <param name="questionId">Question id.</param>
    /// <param name="selectedActionId">Selected <see cref="HumanAction.ActionId"/>.</param>
    /// <param name="selectedActionValue">Resolved <see cref="HumanAction.Value"/>.</param>
    /// <param name="respondentUserId">Platform-native id of the responding operator.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordSelectionAsync(
        string questionId,
        string selectedActionId,
        string selectedActionValue,
        long respondentUserId,
        CancellationToken ct);

    /// <summary>
    /// Returns every question whose <see cref="PendingQuestion.ExpiresAt"/> has
    /// elapsed and that is still in <see cref="PendingQuestionStatus.Pending"/>
    /// or <see cref="PendingQuestionStatus.AwaitingComment"/>. The timeout
    /// processor uses this to fire default-action / timeout side effects and
    /// then transition the rows to <see cref="PendingQuestionStatus.TimedOut"/>.
    /// Returns an empty list (never <see langword="null"/>) when none are due.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct);
}
