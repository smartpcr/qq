using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Storage shape for an <see cref="AgentQuestion"/> sent to Discord and
/// awaiting an operator response. Persisted after the Discord REST API call
/// succeeds and returns a message snowflake id (architecture.md Section 3.1
/// PendingQuestionRecord). Distinct from the <see cref="PendingQuestion"/>
/// DTO returned by <see cref="IPendingQuestionStore"/>: the DTO carries
/// platform-neutral <c>long</c>-cast snowflakes plus the typed
/// <see cref="AgentQuestion"/>; this storage record carries the native
/// <c>ulong</c> snowflakes and a serialized JSON copy of the question.
/// </summary>
public class PendingQuestionRecord
{
    /// <summary>
    /// Stable question identifier (matches <see cref="AgentQuestion.QuestionId"/>).
    /// Primary key; one pending record per logical question.
    /// </summary>
    public string QuestionId { get; set; } = string.Empty;

    /// <summary>Full <see cref="AgentQuestion"/> serialized as JSON.</summary>
    public string AgentQuestion { get; set; } = string.Empty;

    /// <summary>Discord channel snowflake the question was sent to.</summary>
    public ulong DiscordChannelId { get; set; }

    /// <summary>Discord message snowflake of the posted question. Always populated at creation.</summary>
    public ulong DiscordMessageId { get; set; }

    /// <summary>Discord thread snowflake when the question was posted into a thread.</summary>
    public ulong? DiscordThreadId { get; set; }

    /// <summary>
    /// Cached <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/> for
    /// fast timeout handling and highlight rendering.
    /// </summary>
    public string? DefaultActionId { get; set; }

    /// <summary>Resolved <see cref="HumanAction.Value"/> for <see cref="DefaultActionId"/>.</summary>
    public string? DefaultActionValue { get; set; }

    /// <summary>Deadline copied from <see cref="AgentQuestion.ExpiresAt"/>.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Lifecycle state of the pending question. Reuses the shared
    /// <see cref="PendingQuestionStatus"/> enum so the DTO and storage
    /// representations agree on transition semantics.
    /// </summary>
    public PendingQuestionStatus Status { get; set; }

    /// <summary>Action chosen by the operator; <see langword="null"/> until they act.</summary>
    public string? SelectedActionId { get; set; }

    /// <summary>Resolved <see cref="HumanAction.Value"/> for the selected action.</summary>
    public string? SelectedActionValue { get; set; }

    /// <summary>Discord user snowflake of the responding operator; null until they act.</summary>
    public ulong? RespondentUserId { get; set; }

    /// <summary>When the record was first persisted.</summary>
    public DateTimeOffset StoredAt { get; set; }

    /// <summary>End-to-end trace identifier propagated from <see cref="AgentQuestion.CorrelationId"/>.</summary>
    public string CorrelationId { get; set; } = string.Empty;
}
