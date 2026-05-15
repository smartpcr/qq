namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Request to render a release-gate approval card via
/// <see cref="IAdaptiveCardRenderer.RenderReleaseGateCard"/>. <b>Construction order</b>
/// (per <c>architecture.md</c> §6.3.1 multi-approver modeling): the orchestrator FIRST
/// creates per-approver <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/>
/// records (one per required approver, each with its own
/// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion.QuestionId"/> and
/// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion.TargetUserId"/>), THEN
/// constructs one <see cref="ReleaseGateRequest"/> per approver carrying that approver's
/// <c>QuestionId</c> plus the shared gate metadata
/// (<see cref="GateName"/>, <see cref="ReleaseVersion"/>, <see cref="Environment"/>,
/// <see cref="GateConditions"/>, <see cref="GateStatus"/>). Each per-approver
/// <see cref="ReleaseGateRequest"/> is rendered into a separate Adaptive Card so
/// Stage 3.3's <c>CardActionHandler</c> can run the standard single-decision
/// first-writer-wins lifecycle independently for each approver. <see cref="ReleaseGateRequest"/>
/// is therefore a <i>per-approver render input</i>, NOT a shared gate-state record. The
/// release gate card template renders identically for each approver; threshold aggregation
/// (e.g., "2 of 3 approvers must approve") is handled by the orchestrator's workflow layer,
/// NOT by <c>CardActionHandler</c> or the card template itself.
/// </summary>
/// <param name="GateId">Unique release-gate identifier.</param>
/// <param name="QuestionId">Per-approver <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion.QuestionId"/> embedded in the card's action payload. Per <c>architecture.md</c> §6.3.1 (multi-approver release gates), the orchestrator creates one <c>AgentQuestion</c> per required approver and the same <see cref="ReleaseGateRequest"/> metadata is rendered into N cards, each carrying its own per-approver <c>QuestionId</c> so Stage 3.3's <c>CardActionHandler</c> can run the standard single-decision first-writer-wins lifecycle independently for each approver.</param>
/// <param name="TaskId">Associated deployment / release task.</param>
/// <param name="GateName">Human-readable gate name (e.g., "Production Deploy Gate").</param>
/// <param name="ReleaseVersion">The release / build version the gate is guarding (e.g., "v1.42.0", "build-#7421").</param>
/// <param name="Environment">Target environment: <c>Staging</c>, <c>Production</c>, etc.</param>
/// <param name="GateConditions">Ordered checklist of gate conditions and their satisfaction status — rendered as a fact set on the card.</param>
/// <param name="GateStatus">Indicator string for the gate's current state (e.g., <c>Pending</c>, <c>Approved</c>, <c>Rejected</c>, <c>Deferred</c>).</param>
/// <param name="Deadline">Gate expiration deadline.</param>
/// <param name="CorrelationId">End-to-end trace ID.</param>
/// <remarks>
/// <para>
/// <b>Threshold aggregation lives in the orchestrator, not on the card.</b> Per
/// <c>implementation-plan.md</c> §3.1 step 5 and <c>architecture.md</c> §6.3.1, the
/// release-gate card template renders identically for each per-approver
/// <c>AgentQuestion</c>; the "<i>N</i> of <i>M</i> approvers must approve" rollup is
/// computed by the orchestrator's workflow layer and surfaced through
/// <see cref="GateStatus"/> (e.g., <c>Pending</c>, <c>Approved</c>, <c>Rejected</c>),
/// not by carrying approval-counter fields on this record nor by rendering them on
/// the Adaptive Card.
/// </para>
/// </remarks>
public sealed record ReleaseGateRequest(
    string GateId,
    string QuestionId,
    string TaskId,
    string GateName,
    string ReleaseVersion,
    string Environment,
    IReadOnlyList<ReleaseGateCondition> GateConditions,
    string GateStatus,
    DateTimeOffset Deadline,
    string CorrelationId);

/// <summary>
/// A single condition in a release-gate checklist, rendered as a fact on the gate card.
/// </summary>
/// <param name="Name">Display name for the condition (e.g., "All tests pass", "Security scan clean").</param>
/// <param name="Satisfied"><c>true</c> when the condition has been met; <c>false</c> when still outstanding.</param>
public sealed record ReleaseGateCondition(string Name, bool Satisfied);
