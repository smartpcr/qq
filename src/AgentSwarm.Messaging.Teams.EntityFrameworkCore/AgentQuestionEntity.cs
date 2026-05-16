namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core entity backing the <c>AgentQuestions</c> table that persists the full
/// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/> payload, including the
/// serialized <c>AllowedActions</c> list. Aligned with the field list in
/// <c>implementation-plan.md</c> §3.3 step 2 and the architecture contract for
/// <see cref="AgentSwarm.Messaging.Abstractions.IAgentQuestionStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated entity</b>: the platform-agnostic
/// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/> record carries an
/// <c>IReadOnlyList&lt;HumanAction&gt;</c> that EF Core cannot map directly. The store
/// converts the list to a <see cref="AllowedActionsJson"/> blob on write and back to a
/// typed list on read, so the table schema stays flat and indexable.
/// </para>
/// <para>
/// <b>Indexes</b>: see <see cref="TeamsLifecycleDbContext.OnModelCreating"/> for the
/// filtered indexes required by §3.3 step 2 — <c>(ConversationId, Status)
/// WHERE Status = 'Open'</c> for bare <c>approve</c>/<c>reject</c> resolution and
/// <c>(Status, ExpiresAt) WHERE Status = 'Open'</c> for the expiry scan.
/// </para>
/// </remarks>
public sealed class AgentQuestionEntity
{
    /// <summary>Primary key — the originating <c>AgentQuestion.QuestionId</c>.</summary>
    public string QuestionId { get; set; } = string.Empty;

    /// <summary>Originating agent ID. Surfaced on audit entries and used for tenant filtering.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Originating task ID (optional — null when the question is not task-bound).</summary>
    public string? TaskId { get; set; }

    /// <summary>Originating tenant ID — required so proactive routing can pass it to <c>IConversationReferenceStore</c>.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Target user ID (optional — null when the question targets a channel).</summary>
    public string? TargetUserId { get; set; }

    /// <summary>Target channel ID (optional — null when the question targets a user).</summary>
    public string? TargetChannelId { get; set; }

    /// <summary>
    /// Teams conversation ID where the card landed. Populated by
    /// <c>UpdateConversationIdAsync</c> after the proactive send completes; <c>null</c>
    /// until then.
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>Card title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Card body / question text.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Question severity — surfaced in card styling and downstream alerting.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialized <c>IReadOnlyList&lt;HumanAction&gt;</c>. Deserialized on every read
    /// so callers receive the strongly-typed list. Must round-trip lossless.
    /// </summary>
    public string AllowedActionsJson { get; set; } = "[]";

    /// <summary>Deadline after which the question is eligible for expiry.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>End-to-end correlation ID propagated from the originating agent task.</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Lifecycle status — one of <c>AgentQuestionStatuses</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the question was first persisted by <c>SaveAsync</c>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the terminal status transition (Resolved or Expired). Null while Open.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}
