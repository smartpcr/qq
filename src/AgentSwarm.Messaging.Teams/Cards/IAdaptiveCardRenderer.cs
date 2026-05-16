using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Schema;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Renders Teams Adaptive Cards for the platform-agnostic domain types defined in
/// <see cref="AgentSwarm.Messaging.Abstractions"/> and the Teams-specific card payload
/// entities defined in this namespace. Aligned with <c>architecture.md</c> §4.6
/// <c>IAdaptiveCardRenderer</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cross-doc naming.</b> <c>implementation-plan.md</c> §3.1 calls the concrete
/// implementation <c>AdaptiveCardBuilder</c>; this architecture-aligned interface keeps the
/// canonical <c>IAdaptiveCardRenderer</c> contract surface, and the DI wiring is therefore
/// <c>services.AddSingleton&lt;IAdaptiveCardRenderer, AdaptiveCardBuilder&gt;()</c>
/// (per <c>architecture.md</c> §4.6 cross-doc naming note).
/// </para>
/// <para>
/// All <c>Render*</c> methods return a Bot Framework <see cref="Attachment"/> with
/// <c>ContentType = "application/vnd.microsoft.card.adaptive"</c> and an Adaptive Card body
/// suitable for direct attachment to an outbound <see cref="Activity"/>. The card embeds the
/// originating <c>QuestionId</c> / <c>CorrelationId</c> in each <c>Action.Submit.Data</c>
/// payload so <see cref="CardActionMapper"/> can produce a fully-populated
/// <see cref="HumanDecisionEvent"/> on the inbound round-trip.
/// </para>
/// </remarks>
public interface IAdaptiveCardRenderer
{
    /// <summary>
    /// Render an <see cref="AgentQuestion"/> as an Adaptive Card with one
    /// <c>Action.Submit</c> per <see cref="AgentQuestion.AllowedActions"/> entry. When any
    /// allowed action declares <see cref="HumanAction.RequiresComment"/>, the card includes
    /// an <c>Input.Text</c> field (Id <c>comment</c>) so the user can supply the comment in
    /// the same submit.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="question"/> is <c>null</c>.</exception>
    Attachment RenderQuestionCard(AgentQuestion question);

    /// <summary>
    /// Render an <see cref="AgentStatusSummary"/> as a status / progress Adaptive Card. The
    /// card is informational only — no action buttons are emitted.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="status"/> is <c>null</c>.</exception>
    Attachment RenderStatusCard(AgentStatusSummary status);

    /// <summary>
    /// Render an <see cref="IncidentSummary"/> as an incident-escalation Adaptive Card with
    /// <c>Escalate</c> and <c>Acknowledge</c> action buttons.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="incident"/> is <c>null</c>.</exception>
    Attachment RenderIncidentCard(IncidentSummary incident);

    /// <summary>
    /// Render a <see cref="ReleaseGateRequest"/> as a release-gate Adaptive Card with
    /// <c>Approve</c>, <c>Reject</c>, and <c>Defer</c> action buttons and a checklist of
    /// gate conditions.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> is <c>null</c>.</exception>
    Attachment RenderReleaseGateCard(ReleaseGateRequest gate);

    /// <summary>
    /// Render a read-only confirmation card summarising a recorded
    /// <see cref="HumanDecisionEvent"/>. Used by <c>ITeamsCardManager.UpdateCardAsync</c>
    /// (Stage 3.3) to replace the original card after a decision has been captured.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <c>null</c>.</exception>
    Attachment RenderDecisionConfirmationCard(HumanDecisionEvent decision);

    /// <summary>
    /// Render a read-only confirmation card that carries an actor display name in the
    /// header (for example, <c>"Approved by Alice Wong"</c>). Used by
    /// <c>CardActionHandler</c> (Stage 3.3) when the inbound turn context exposed a
    /// human-readable name for the actor — when only the AAD object ID is available the
    /// caller passes <c>null</c> for <paramref name="actorDisplayName"/> and this method
    /// falls back to the AAD object ID.
    /// </summary>
    /// <param name="decision">The recorded decision payload.</param>
    /// <param name="actorDisplayName">Friendly display name of the responding human; null/empty falls back to <see cref="HumanDecisionEvent.ExternalUserId"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <c>null</c>.</exception>
    Attachment RenderDecisionConfirmationCard(HumanDecisionEvent decision, string? actorDisplayName);

    /// <summary>
    /// Render a read-only notice replacing the original card after the question expired
    /// before any human responded. Used by <c>ITeamsCardManager.UpdateCardAsync</c>
    /// with <see cref="CardUpdateAction.MarkExpired"/>.
    /// </summary>
    /// <param name="questionId">Originating question ID — surfaced on the replacement card for traceability.</param>
    Attachment RenderExpiredNoticeCard(string questionId);

    /// <summary>
    /// Render a read-only notice replacing the original card after the question was
    /// cancelled (for example, the underlying agent task was withdrawn). Used by
    /// <c>ITeamsCardManager.UpdateCardAsync</c> with
    /// <see cref="CardUpdateAction.MarkCancelled"/>.
    /// </summary>
    /// <param name="questionId">Originating question ID — surfaced on the replacement card for traceability.</param>
    Attachment RenderCancelledNoticeCard(string questionId);
}
