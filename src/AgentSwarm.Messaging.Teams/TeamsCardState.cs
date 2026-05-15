namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persisted state for an Adaptive Card (or text reply) sent to Teams in response to an
/// <c>AgentQuestion</c>. Captures the Teams <c>activityId</c> required for
/// <c>UpdateActivityAsync</c> / <c>DeleteActivityAsync</c> calls and the serialized
/// <c>ConversationReference</c> needed to rehydrate a proactive turn context in a
/// background worker.
/// </summary>
/// <remarks>
/// Field set aligned with <c>implementation-plan.md</c> §2.1 step 3. The <see cref="QuestionId"/>
/// serves as the natural primary key (one card per question per delivery) — consistent with
/// <c>ICardStateStore.GetByQuestionIdAsync</c> and <c>UpdateStatusAsync(questionId, ...)</c>
/// in the same step.
/// </remarks>
public sealed record TeamsCardState
{
    /// <summary>Linked <c>AgentQuestion.QuestionId</c>. Natural primary key.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Teams activity ID returned by <c>SendActivityAsync</c> — needed for update/delete.</summary>
    public required string ActivityId { get; init; }

    /// <summary>Bot Framework conversation ID where the card was delivered.</summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Serialized Bot Framework <c>ConversationReference</c> — captured from the proactive
    /// turn context at send time so background workers can rehydrate a callback target
    /// without re-querying <see cref="IConversationReferenceStore"/>. Serialization MUST use
    /// <c>Newtonsoft.Json</c> to preserve the Bot Framework wire-name contract (camelCase
    /// property names, <c>JObject</c> extension data, etc.).
    /// </summary>
    public required string ConversationReferenceJson { get; init; }

    /// <summary>Lifecycle state — one of <see cref="TeamsCardStatuses.All"/>. Defaults to <see cref="TeamsCardStatuses.Pending"/>.</summary>
    public string Status { get; init; } = TeamsCardStatuses.Pending;

    /// <summary>UTC timestamp when the card was delivered.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp of the most recent <c>Status</c> change.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
