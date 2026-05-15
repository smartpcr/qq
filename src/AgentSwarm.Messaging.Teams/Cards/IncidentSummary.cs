using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Summary of an incident for rendering an incident-alert card via
/// <see cref="IAdaptiveCardRenderer.RenderIncidentCard"/>. Aligned with
/// <c>architecture.md</c> §3.3 <c>IncidentSummary</c> field table.
/// </summary>
/// <param name="IncidentId">Unique incident identifier.</param>
/// <param name="QuestionId">Per-acknowledger <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion.QuestionId"/> embedded in the card's action payload so Stage 3.3's <c>CardActionHandler</c> can resolve the originating question on the inbound round-trip (per <c>architecture.md</c> §6.3 step 4). Each acknowledger / escalator gets their own per-recipient <c>AgentQuestion</c> following the standard single-decision lifecycle.</param>
/// <param name="TaskId">Originating task.</param>
/// <param name="AgentId">Agent that raised the incident.</param>
/// <param name="AffectedAgents">Identifiers of all agents impacted by this incident — typically a superset of <see cref="AgentId"/>.</param>
/// <param name="Severity">One of <see cref="MessageSeverities.All"/>.</param>
/// <param name="Title">Short incident title for the card header.</param>
/// <param name="Description">Detailed incident description.</param>
/// <param name="OccurredAt">UTC time the incident was detected.</param>
/// <param name="CorrelationId">End-to-end trace ID.</param>
public sealed record IncidentSummary(
    string IncidentId,
    string QuestionId,
    string TaskId,
    string AgentId,
    IReadOnlyList<string> AffectedAgents,
    string Severity,
    string Title,
    string Description,
    DateTimeOffset OccurredAt,
    string CorrelationId);
