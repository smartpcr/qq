namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A single action a human can take in response to an
/// <see cref="AgentQuestion"/>. Each action becomes a Block Kit button on
/// Slack; if <see cref="RequiresComment"/> is <c>true</c> the button opens
/// a modal with a free-form text input instead of submitting directly.
/// </summary>
/// <remarks>
/// COMPILE STUB. Field contract mirrors section 3.6.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/architecture.md</c>:
/// <c>HumanAction(ActionId, Label, Value, RequiresComment)</c>.
/// </remarks>
/// <param name="ActionId">Stable identifier for the action (used in block_id encoding).</param>
/// <param name="Label">Display text for the button.</param>
/// <param name="Value">Machine-readable value propagated as <c>ActionValue</c> on the resulting <see cref="HumanDecisionEvent"/>.</param>
/// <param name="RequiresComment">When <c>true</c>, clicking the button opens a comment modal before submission.</param>
public sealed record HumanAction(
    string ActionId,
    string Label,
    string Value,
    bool RequiresComment);
