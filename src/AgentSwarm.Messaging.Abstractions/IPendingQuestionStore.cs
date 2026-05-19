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

    /// <summary>
    /// <para>
    /// <b>Atomic claim primitive.</b> Transitions the question to
    /// <see cref="PendingQuestionStatus.Answered"/> via a single
    /// row-level conditional update (provider-neutral; implemented via
    /// EF Core's <c>ExecuteUpdateAsync</c> against the persistent
    /// store, and
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryUpdate"/>
    /// against the in-memory stub).
    /// </para>
    /// <para>
    /// Returns <see langword="true"/> only when THIS caller actually
    /// moved the row from <see cref="PendingQuestionStatus.Pending"/>
    /// or <see cref="PendingQuestionStatus.AwaitingComment"/> to
    /// <see cref="PendingQuestionStatus.Answered"/>. Returns
    /// <see langword="false"/> when the row is missing or already in a
    /// terminal state (e.g. <c>QuestionTimeoutService</c> won the
    /// claim between the callback handler's snapshot read and the
    /// mark, or a duplicate webhook delivery already terminal-d the
    /// row). <c>CallbackQueryHandler</c> calls this method
    /// <b>BEFORE</b> publishing
    /// <see cref="HumanDecisionEvent"/> so the cross-process
    /// callback-vs-timeout race is closed: only the winning
    /// claimant publishes (Stage 5.3 iter-3 evaluator item 7).
    /// </para>
    /// </summary>
    Task<bool> MarkAnsweredAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// <para>
    /// <b>Atomic claim primitive.</b> Transitions the question to
    /// <see cref="PendingQuestionStatus.AwaitingComment"/> via a
    /// single row-level conditional update — the only legal source
    /// state for this transition is
    /// <see cref="PendingQuestionStatus.Pending"/>. Invoked by the
    /// callback handler when the tapped action has
    /// <see cref="HumanAction.RequiresComment"/> set.
    /// </para>
    /// <para>
    /// Returns <see langword="true"/> only when THIS caller actually
    /// moved the row to
    /// <see cref="PendingQuestionStatus.AwaitingComment"/>. Returns
    /// <see langword="false"/> when the row is missing or had already
    /// been transitioned by another process (typically
    /// <see cref="PendingQuestionStatus.TimedOut"/> by a sweep, or
    /// <see cref="PendingQuestionStatus.Answered"/> by a duplicate
    /// callback delivery). Callers MUST branch on the returned bool
    /// and skip the "please send a comment" prompt when this returns
    /// <see langword="false"/> so the operator does not get prompted
    /// for text against a question the system has already defaulted
    /// (Stage 5.3 iter-3 evaluator item 7).
    /// </para>
    /// </summary>
    Task<bool> MarkAwaitingCommentAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// <para>
    /// <b>Atomic claim primitive.</b> Transitions the question to
    /// <see cref="PendingQuestionStatus.TimedOut"/> in a single
    /// row-level conditional update (provider-neutral; implemented via
    /// EF Core's <c>ExecuteUpdateAsync</c> against the persistent
    /// store, and <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryUpdate"/>
    /// against the in-memory stub).
    /// </para>
    /// <para>
    /// Returns <see langword="true"/> only when THIS caller actually
    /// moved the row from <see cref="PendingQuestionStatus.Pending"/>
    /// or <see cref="PendingQuestionStatus.AwaitingComment"/> to
    /// <see cref="PendingQuestionStatus.TimedOut"/>. Returns
    /// <see langword="false"/> when the row is missing or already in a
    /// terminal state (e.g. another worker beat us to it, or the
    /// operator answered between <c>GetExpiredAsync</c> and the claim
    /// attempt). <c>QuestionTimeoutService</c> calls this method
    /// <b>BEFORE</b> publishing
    /// <see cref="HumanDecisionEvent"/> so a cross-process sweep race
    /// cannot double-publish — only the winning claimant publishes.
    /// Per architecture.md §10.3 the claim is the cross-process
    /// concurrency primitive; the polling query in
    /// <see cref="GetExpiredAsync"/> is a snapshot that races with
    /// every other sweeper running against the same store.
    /// </para>
    /// <para>
    /// <b>Compensation pairing.</b> The architecture's documented
    /// timeout flow (architecture.md §10.3 steps 1–4) requires that
    /// <see cref="HumanDecisionEvent"/> emission MUST NOT be lost on
    /// publish failure. The claim-first ordering closes the iter-1
    /// double-publish race but, on its own, would leave the row
    /// terminally <see cref="PendingQuestionStatus.TimedOut"/> if the
    /// subsequent publish throws — permanently dropping the decision
    /// (iter-2 evaluator item 5). The fix is the paired
    /// <see cref="TryRevertTimedOutClaimAsync"/> primitive below:
    /// <c>QuestionTimeoutService</c> wraps publish in try / catch and
    /// reverts the claim on failure so the next sweep retries. Together
    /// the two methods give claim-first race protection AND
    /// at-least-once timeout emission.
    /// </para>
    /// </summary>
    Task<bool> MarkTimedOutAsync(string questionId, CancellationToken ct);

    /// <summary>
    /// <para>
    /// <b>Compensation primitive.</b> Conditionally transitions a row
    /// from <see cref="PendingQuestionStatus.TimedOut"/> back to a
    /// non-terminal status (<see cref="PendingQuestionStatus.Pending"/>
    /// or <see cref="PendingQuestionStatus.AwaitingComment"/>) in a
    /// single row-level CAS-style update. The complement of
    /// <see cref="MarkTimedOutAsync"/> — together they let
    /// <c>QuestionTimeoutService</c> hold an atomic claim for the
    /// duration of a publish attempt and release it (so the next sweep
    /// retries) when the publish throws. Without this compensation
    /// step, a publish failure after a successful claim would
    /// permanently drop the timeout decision (iter-2 evaluator item 5
    /// — architecture.md §10.3 requires the decision is emitted at
    /// least once).
    /// </para>
    /// <para>
    /// Returns <see langword="true"/> only when THIS caller actually
    /// moved the row from <see cref="PendingQuestionStatus.TimedOut"/>
    /// to <paramref name="revertTo"/>. Returns <see langword="false"/>
    /// when the row is missing or is no longer in
    /// <see cref="PendingQuestionStatus.TimedOut"/> (e.g. another
    /// worker observed the orphan and a separate process already
    /// progressed it). The CAS guarantees no clobber of any
    /// state-transition another caller has already performed.
    /// </para>
    /// <para>
    /// <paramref name="revertTo"/> must be either
    /// <see cref="PendingQuestionStatus.Pending"/> or
    /// <see cref="PendingQuestionStatus.AwaitingComment"/>; callers
    /// should pass the snapshot's pre-claim
    /// <see cref="PendingQuestion.Status"/> so the original operator
    /// context (AwaitingComment in particular) is preserved on the
    /// next sweep. Any other value throws
    /// <see cref="System.ArgumentOutOfRangeException"/>.
    /// </para>
    /// </summary>
    Task<bool> TryRevertTimedOutClaimAsync(
        string questionId,
        PendingQuestionStatus revertTo,
        CancellationToken ct);

    /// <summary>
    /// <para>
    /// <b>Compensation primitive.</b> Conditionally transitions a row
    /// from <see cref="PendingQuestionStatus.Answered"/> back to
    /// <see cref="PendingQuestionStatus.Pending"/> in a single
    /// row-level CAS-style update. The complement of
    /// <see cref="MarkAnsweredAsync"/> — together they let
    /// <c>CallbackQueryHandler</c> /
    /// <c>DecisionCommandHandlers</c> hold an atomic claim for the
    /// duration of a publish+audit attempt and release it (so a
    /// retry — webhook redelivery, operator re-tap, slash-command
    /// re-issue — can re-process the same question) when the
    /// publish or audit throws. Without this compensation step a
    /// claim-first ordering would permanently drop a decision on
    /// the first publish/audit failure (Stage 5.3 iter-3 evaluator
    /// items 6 + 7 combined).
    /// </para>
    /// <para>
    /// Returns <see langword="true"/> only when THIS caller actually
    /// moved the row from <see cref="PendingQuestionStatus.Answered"/>
    /// back to <see cref="PendingQuestionStatus.Pending"/>. Returns
    /// <see langword="false"/> when the row is missing or is no
    /// longer in <see cref="PendingQuestionStatus.Answered"/>
    /// (e.g. a parallel process already progressed it). The CAS
    /// guarantees no clobber of any state-transition another
    /// caller has already performed.
    /// </para>
    /// </summary>
    Task<bool> TryRevertAnsweredClaimAsync(
        string questionId,
        CancellationToken ct);

    /// <summary>
    /// <para>
    /// <b>Compensation primitive.</b> Conditionally transitions a row
    /// from <see cref="PendingQuestionStatus.AwaitingComment"/> back
    /// to <see cref="PendingQuestionStatus.Pending"/> in a single
    /// row-level CAS-style update. Paired with
    /// <see cref="MarkAwaitingCommentAsync"/>: if the post-claim
    /// "please send a comment" prompt fails to send, the handler
    /// releases the claim so the operator's next tap can re-claim
    /// the slot.
    /// </para>
    /// </summary>
    Task<bool> TryRevertAwaitingCommentClaimAsync(
        string questionId,
        CancellationToken ct);

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
