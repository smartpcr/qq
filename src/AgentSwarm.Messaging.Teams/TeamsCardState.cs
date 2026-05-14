namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Persisted state for a single Adaptive Card delivered for an <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/>.
/// Co-located with <see cref="ICardStateStore"/> in <c>AgentSwarm.Messaging.Teams</c> to avoid
/// a circular Abstractions → Teams dependency since the store contract accepts this record as
/// a parameter (per <c>architecture.md</c> §4.3 and §7).
/// </summary>
public sealed record TeamsCardState
{
    /// <summary>Question identifier — primary key on the question/card join.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Teams activity ID returned by the proactive send; used for follow-up
    /// <c>UpdateActivityAsync</c> / <c>DeleteActivityAsync</c> calls.</summary>
    public required string ActivityId { get; init; }

    /// <summary>Teams conversation ID hosting the card.</summary>
    public required string ConversationId { get; init; }

    /// <summary>JSON-serialized <c>ConversationReference</c> for background rehydration in
    /// <c>CloudAdapter.ContinueConversationAsync</c>.</summary>
    public required string ConversationReferenceJson { get; init; }

    /// <summary>One of <c>Pending</c>, <c>Answered</c>, <c>Expired</c>.</summary>
    public required string Status { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC last-update timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
