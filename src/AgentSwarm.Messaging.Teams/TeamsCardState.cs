namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Tracks the state of an Adaptive Card sent to Teams. Persisted so the Teams connector can
/// later update or delete the card via Bot Framework <c>UpdateActivityAsync</c> /
/// <c>DeleteActivityAsync</c>. Aligned with <c>architecture.md</c> §3.2 and Stage 2.1.
/// </summary>
/// <remarks>
/// <para>
/// Co-located with <see cref="ICardStateStore"/> in <c>AgentSwarm.Messaging.Teams</c> to
/// avoid a circular Abstractions → Teams dependency
/// (<see cref="ICardStateStore.SaveAsync"/> takes <see cref="TeamsCardState"/>).
/// </para>
/// <para>
/// <see cref="Status"/> follows the canonical vocabulary <c>Pending</c> / <c>Answered</c> /
/// <c>Expired</c>.
/// </para>
/// </remarks>
public sealed record TeamsCardState
{
    /// <summary>Linked <c>AgentQuestion.QuestionId</c>. Primary lookup key for update/delete.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Teams activity (message) ID of the sent card.</summary>
    public required string ActivityId { get; init; }

    /// <summary>Bot Framework conversation ID where the card was delivered.</summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Serialized Bot Framework <c>ConversationReference</c> required by background workers
    /// for rehydration prior to calling <c>UpdateActivityAsync</c> / <c>DeleteActivityAsync</c>.
    /// </summary>
    public required string ConversationReferenceJson { get; init; }

    /// <summary>Card lifecycle status. One of <c>Pending</c>, <c>Answered</c>, <c>Expired</c>.</summary>
    public required string Status { get; init; }

    /// <summary>UTC time the card was sent.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC time of the last status change.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
