namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Manages the lifecycle of pending agent questions awaiting an operator
/// response. Methods return the <see cref="PendingQuestion"/> abstraction DTO
/// so consumers in connector projects do not depend on the persistence
/// assembly. See architecture.md §4.7 for full semantics.
/// </summary>
public interface IPendingQuestionStore
{
    /// <summary>
    /// Persist a freshly-sent question. The store extracts every
    /// <see cref="AgentQuestion"/> field — including
    /// <see cref="AgentQuestion.TaskId"/> and <see cref="AgentQuestion.Severity"/>
    /// onto the corresponding <see cref="PendingQuestion"/> properties — and
    /// denormalizes <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>
    /// into <see cref="PendingQuestion.DefaultActionId"/>, resolving the
    /// corresponding <see cref="HumanAction.Value"/> into
    /// <see cref="PendingQuestion.DefaultActionValue"/>.
    /// </summary>
    Task StoreAsync(
        AgentQuestionEnvelope envelope,
        long telegramChatId,
        long telegramMessageId,
        CancellationToken ct);

    /// <summary>
    /// Primary callback lookup path. Uses the <see cref="PendingQuestion.QuestionId"/>
    /// primary key and is immune to cross-chat <c>message_id</c> collisions.
    /// </summary>
    Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Composite <c>(TelegramChatId, TelegramMessageId)</c> lookup. Telegram
    /// <c>message_id</c> is only unique within a chat. Used only by
    /// <c>QuestionRecoverySweep</c> for backfill correlation, not for
    /// callback resolution.
    /// </summary>
    Task<PendingQuestion?> GetByTelegramMessageAsync(
        long telegramChatId,
        long telegramMessageId,
        CancellationToken ct);

    /// <summary>Transitions the question to <see cref="PendingQuestionStatus.Answered"/>.</summary>
    Task MarkAnsweredAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Transitions the question to <see cref="PendingQuestionStatus.AwaitingComment"/>.
    /// Invoked by the callback handler when the tapped action requires a
    /// follow-up comment.
    /// </summary>
    Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// Persist the operator's tapped selection, its canonical
    /// <see cref="HumanAction.Value"/>, and Telegram user ID on the pending
    /// question record. Invoked by <c>CallbackQueryHandler</c> before
    /// <see cref="MarkAwaitingCommentAsync"/>.
    /// </summary>
    Task RecordSelectionAsync(
        string questionId,
        string selectedActionId,
        string selectedActionValue,
        long respondentUserId,
        CancellationToken ct);

    /// <summary>
    /// Returns the oldest pending question (by <see cref="PendingQuestion.StoredAt"/>)
    /// with <see cref="PendingQuestionStatus.AwaitingComment"/> for the given
    /// <c>(TelegramChatId, RespondentUserId)</c> pair.
    /// </summary>
    Task<PendingQuestion?> GetAwaitingCommentAsync(
        long telegramChatId,
        long respondentUserId,
        CancellationToken ct);

    /// <summary>
    /// All questions in <see cref="PendingQuestionStatus.Pending"/> or
    /// <see cref="PendingQuestionStatus.AwaitingComment"/> whose
    /// <see cref="PendingQuestion.ExpiresAt"/> is in the past. Used by
    /// <c>QuestionTimeoutService</c> for default-action application.
    /// </summary>
    Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct);
}
