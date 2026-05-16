// -----------------------------------------------------------------------
// <copyright file="PendingQuestionRecord.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Stage 3.5 — EF Core persistence entity that backs
/// <see cref="IPendingQuestionStore"/>. One row per
/// <see cref="AgentQuestion.QuestionId"/> that has been successfully
/// sent to Telegram and is awaiting an operator response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placement rationale.</b> The entity lives in
/// <c>AgentSwarm.Messaging.Persistence</c>, NOT in
/// <c>AgentSwarm.Messaging.Telegram</c>, so that the dependency arrow
/// flows from <c>Persistence → Abstractions</c> rather than
/// <c>Telegram → Persistence</c>. Callers in the connector consume
/// the abstraction DTO <see cref="PendingQuestion"/> via
/// <see cref="IPendingQuestionStore"/> and never reference this type
/// directly — see implementation-plan.md Stage 3.5 brief and
/// architecture.md §3.1.
/// </para>
/// <para>
/// <b>Denormalisation strategy.</b> The full
/// <see cref="AgentQuestion"/> is stored as JSON in
/// <see cref="AgentQuestionJson"/> so the connector can reconstruct
/// every field of the abstraction DTO (including non-indexed fields:
/// <c>AgentId</c>, <c>TaskId</c>, <c>Title</c>, <c>Body</c>,
/// <c>AllowedActions</c>). The hot-path / filterable columns —
/// <see cref="ExpiresAt"/>, <see cref="Status"/>,
/// <see cref="TelegramChatId"/>, <see cref="TelegramMessageId"/>,
/// <see cref="DefaultActionValue"/>, <see cref="CorrelationId"/> — are
/// also persisted as individual columns so the timeout polling query
/// (<see cref="IPendingQuestionStore.GetExpiredAsync"/>) and the
/// comment-correlation query
/// (<see cref="IPendingQuestionStore.GetAwaitingCommentAsync"/>) can
/// use indexes without paying for JSON deserialization on every row.
/// </para>
/// <para>
/// <b>Default-action denormalisation.</b>
/// <see cref="DefaultActionId"/> mirrors
/// <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>; the
/// resolved <see cref="HumanAction.Value"/> is denormalised into
/// <see cref="DefaultActionValue"/> at <c>StoreAsync</c> time so
/// <see cref="Telegram.QuestionTimeoutService"/> can emit a
/// <see cref="HumanDecisionEvent"/> with the canonical
/// <c>ActionValue</c> WITHOUT consulting <c>IDistributedCache</c>
/// (the cache entry expires at <c>AgentQuestion.ExpiresAt + 5 min</c>
/// and is likely evicted by the time the timeout fires — see
/// architecture.md §10.3).
/// </para>
/// </remarks>
public sealed class PendingQuestionRecord
{
    /// <summary>Primary key — matches <see cref="AgentQuestion.QuestionId"/>.</summary>
    public required string QuestionId { get; set; }

    /// <summary>
    /// Full <see cref="AgentQuestion"/> serialized as JSON. Preserves
    /// complete question context for display / audit / timeout
    /// rendering without a secondary lookup, AND for cache-miss
    /// fallback in the callback handler (architecture.md §5.2 invariant 3).
    /// </summary>
    public required string AgentQuestionJson { get; set; }

    /// <summary>Telegram chat the question was sent to.</summary>
    public required long TelegramChatId { get; set; }

    /// <summary>
    /// Telegram <c>message_id</c> of the inline-keyboard message.
    /// Always populated at row creation because the record is only
    /// persisted after a successful Telegram send.
    /// </summary>
    public required long TelegramMessageId { get; set; }

    /// <summary>
    /// Copied from <see cref="AgentQuestion.ExpiresAt"/> for efficient
    /// timeout polling — indexed with <see cref="Status"/>.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Wall-clock timestamp at which the record was persisted after a
    /// successful Telegram send. Used for deterministic oldest-first
    /// tie-breaking in
    /// <see cref="IPendingQuestionStore.GetAwaitingCommentAsync"/>.
    /// </summary>
    public required DateTimeOffset StoredAt { get; set; }

    /// <summary>
    /// Denormalised from
    /// <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>. Used
    /// for display / audit and for <c>QuestionRecoverySweep</c>
    /// backfill correlation. <see langword="null"/> when the envelope
    /// did not propose a default.
    /// </summary>
    public string? DefaultActionId { get; set; }

    /// <summary>
    /// Denormalised <see cref="HumanAction.Value"/> of the default
    /// action — resolved by looking up the <see cref="HumanAction"/>
    /// in <see cref="AgentQuestion.AllowedActions"/> whose
    /// <see cref="HumanAction.ActionId"/> matches
    /// <see cref="DefaultActionId"/>. Read directly by
    /// <see cref="Telegram.QuestionTimeoutService"/> at timeout to
    /// emit <see cref="HumanDecisionEvent.ActionValue"/> without a
    /// cache lookup (architecture.md §10.3).
    /// </summary>
    public string? DefaultActionValue { get; set; }

    /// <summary>
    /// Set by <see cref="IPendingQuestionStore.RecordSelectionAsync"/>
    /// when the operator taps an inline button. Identifies the
    /// <see cref="HumanAction.ActionId"/> that was selected.
    /// </summary>
    public string? SelectedActionId { get; set; }

    /// <summary>
    /// Resolved <see cref="HumanAction.Value"/> of the selected
    /// action. Persisted alongside <see cref="SelectedActionId"/> so
    /// the <c>RequiresComment</c> text-reply path can emit
    /// <see cref="HumanDecisionEvent.ActionValue"/> from durable
    /// storage when the volatile <c>IDistributedCache</c> entry has
    /// already expired (architecture.md §3.1).
    /// </summary>
    public string? SelectedActionValue { get; set; }

    /// <summary>
    /// Telegram user id of the operator who tapped the inline button.
    /// Set together with <see cref="SelectedActionId"/>. Used with
    /// <see cref="TelegramChatId"/> and
    /// <see cref="PendingQuestionStatus.AwaitingComment"/> to correlate
    /// follow-up text replies.
    /// </summary>
    public long? RespondentUserId { get; set; }

    /// <summary>
    /// Lifecycle status — see <see cref="PendingQuestionStatus"/>.
    /// Stored as string for SQL legibility.
    /// </summary>
    public required PendingQuestionStatus Status { get; set; }

    /// <summary>Trace / correlation id for end-to-end observability.</summary>
    public required string CorrelationId { get; set; }
}
