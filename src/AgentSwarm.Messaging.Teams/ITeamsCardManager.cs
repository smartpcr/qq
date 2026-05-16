using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Teams-specific card lifecycle contract for already-sent Adaptive Cards. Aligned with
/// <c>architecture.md</c> §4.1.1 — the canonical implementation is
/// <see cref="TeamsMessengerConnector"/>, which exposes both
/// <see cref="AgentSwarm.Messaging.Abstractions.IMessengerConnector"/> and this interface
/// behind the same singleton (the connector owns the proactive
/// <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/> and the stored
/// <see cref="TeamsCardState"/> already required for outbound delivery).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="UpdateCardAsync(string, CardUpdateAction, System.Threading.CancellationToken)"/>
/// overload is the canonical contract callers code against (per
/// <c>implementation-plan.md</c> §3.3 step 4 — "<c>CardActionHandler</c> calls
/// <c>UpdateCardAsync(questionId, CardUpdateAction.MarkAnswered)</c>"). The
/// decision-attributed overload below is a second entry point used by
/// <see cref="AgentSwarm.Messaging.Teams.Cards.CardActionHandler"/> (Stage 3.3) to render
/// a confirmation card that includes the chosen action and the responding actor; it does
/// NOT replace the canonical 3-arg method.
/// </para>
/// <para>
/// <b>Stale-activity fallback (per <c>e2e-scenarios.md</c>).</b> When the underlying
/// Bot Framework <c>UpdateActivityAsync</c> reports the activity is no longer present
/// (HTTP 404), implementations send a fresh replacement card and persist the new
/// <c>ActivityId</c> via <see cref="ICardStateStore.SaveAsync"/>'s upsert semantics so the
/// card lifecycle remains consistent. Card-state status remains <c>Answered</c>.
/// </para>
/// <para>
/// <b>Inline retry.</b> Both <see cref="UpdateCardAsync(string, CardUpdateAction, System.Threading.CancellationToken)"/>
/// and <see cref="DeleteCardAsync"/> apply the canonical Teams retry policy from
/// <c>tech-spec.md</c> §4.4 (5 total attempts, base 2s delay, ±25% jitter, honour
/// <c>Retry-After</c>) inline rather than enqueueing through the outbox — these
/// single-activity mutations target an existing <c>activityId</c> that must be acted on
/// promptly.
/// </para>
/// </remarks>
public interface ITeamsCardManager
{
    /// <summary>
    /// Replace the previously-sent card associated with <paramref name="questionId"/>. The
    /// concrete <see cref="CardUpdateAction"/> selects the replacement card content
    /// (<c>MarkAnswered</c> → resolved confirmation, <c>MarkExpired</c> → expired notice,
    /// <c>MarkCancelled</c> → cancelled notice).
    /// </summary>
    /// <param name="questionId">Originating <c>AgentQuestion.QuestionId</c>.</param>
    /// <param name="action">The lifecycle update to render.</param>
    /// <param name="ct">Cancellation token observed by the inline retry loop.</param>
    Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct);

    /// <summary>
    /// Decision-attributed overload used by
    /// <see cref="AgentSwarm.Messaging.Teams.Cards.CardActionHandler"/>: replaces the
    /// original card with a confirmation card that surfaces the chosen
    /// <see cref="HumanDecisionEvent.ActionValue"/> and a human-friendly actor label
    /// ("Approved by …"). The canonical
    /// <see cref="UpdateCardAsync(string, CardUpdateAction, System.Threading.CancellationToken)"/>
    /// remains the contract callers without a decision payload code against; this
    /// overload is additive (per the architecture.md §4.1.1 contract — the canonical
    /// shape is preserved).
    /// </summary>
    /// <param name="questionId">Originating <c>AgentQuestion.QuestionId</c>.</param>
    /// <param name="action">The lifecycle update to render — typically <see cref="CardUpdateAction.MarkAnswered"/>.</param>
    /// <param name="decision">The recorded human decision; used to populate action and actor on the replacement card.</param>
    /// <param name="actorDisplayName">Optional friendly display name (e.g. <c>turnContext.Activity.From.Name</c>); falls back to <see cref="HumanDecisionEvent.ExternalUserId"/> when null/empty.</param>
    /// <param name="ct">Cancellation token observed by the inline retry loop.</param>
    Task UpdateCardAsync(
        string questionId,
        CardUpdateAction action,
        HumanDecisionEvent decision,
        string? actorDisplayName,
        CancellationToken ct);

    /// <summary>
    /// Delete the previously-sent card associated with <paramref name="questionId"/>. On
    /// success card-state <see cref="TeamsCardState.Status"/> is updated to
    /// <see cref="TeamsCardStatuses.Expired"/> per the §3.3 implementation-plan spec
    /// (lines 213, 222). The implementation does NOT introduce a separate
    /// <c>Deleted</c> status — the canonical card vocabulary remains
    /// Pending/Answered/Expired.
    /// </summary>
    /// <param name="questionId">Originating <c>AgentQuestion.QuestionId</c>.</param>
    /// <param name="ct">Cancellation token observed by the inline retry loop.</param>
    Task DeleteCardAsync(string questionId, CancellationToken ct);
}

/// <summary>
/// Discriminator for the replacement-card content emitted by
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, System.Threading.CancellationToken)"/>
/// per <c>architecture.md</c> §4.1.1.
/// </summary>
public enum CardUpdateAction
{
    /// <summary>The originating question was answered — render a resolved confirmation.</summary>
    MarkAnswered,

    /// <summary>The originating question expired — render an expired notice.</summary>
    MarkExpired,

    /// <summary>The originating question was cancelled — render a cancelled notice. Card-state lands at <see cref="TeamsCardStatuses.Expired"/> (terminal — no separate Cancelled status is defined).</summary>
    MarkCancelled,
}
