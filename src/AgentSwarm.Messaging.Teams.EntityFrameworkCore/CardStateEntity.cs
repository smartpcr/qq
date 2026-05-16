namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core entity backing the <c>CardStates</c> table that persists the Teams-specific
/// message identity (<see cref="ActivityId"/>) and conversation rehydration data
/// (<see cref="ConversationReferenceJson"/>) for each Adaptive Card sent in response to
/// an <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/>. Aligned with the
/// field list in <c>implementation-plan.md</c> §3.3 step 1 and the architecture contract
/// for <see cref="AgentSwarm.Messaging.Teams.ICardStateStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated entity</b>: the platform-facing <see cref="TeamsCardState"/>
/// record uses <c>required init</c> properties and is therefore awkward to map directly
/// to EF Core (the framework needs settable surrogate properties on entity types).
/// <see cref="AgentSwarm.Messaging.Teams.EntityFrameworkCore.SqlCardStateStore"/>
/// converts between the two on the way in and out.
/// </para>
/// <para>
/// <b>Canonical status vocabulary</b>: <c>Pending</c>, <c>Answered</c>, <c>Expired</c>
/// (see <see cref="TeamsCardStatuses.All"/>). A deleted card lands at
/// <c>Expired</c> per <c>implementation-plan.md</c> §3.3 step 5 — there is no separate
/// <c>Deleted</c> status. This keeps the card-state vocabulary stable across the
/// lifetime of the project and avoids widening
/// <see cref="AgentSwarm.Messaging.Teams.ICardStateStore"/>.
/// </para>
/// </remarks>
public sealed class CardStateEntity
{
    /// <summary>Primary key — the originating <c>AgentQuestion.QuestionId</c>.</summary>
    public string QuestionId { get; set; } = string.Empty;

    /// <summary>Teams activity ID returned by <c>SendActivityAsync</c> — needed for update/delete.</summary>
    public string ActivityId { get; set; } = string.Empty;

    /// <summary>Bot Framework conversation ID where the card was delivered.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Serialized Bot Framework <c>ConversationReference</c> captured at send time.</summary>
    public string ConversationReferenceJson { get; set; } = string.Empty;

    /// <summary>Lifecycle status — one of <c>TeamsCardStatuses.All</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the card was first persisted by <c>SaveAsync</c>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent <c>Status</c> change.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
