namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A single action button rendered on an <see cref="AgentQuestion"/> card. Defines the
/// machine-readable value that flows back as <c>HumanDecisionEvent.ActionValue</c> when the
/// human presses the button.
/// </summary>
/// <param name="ActionId">Unique action identifier scoped to the parent question.</param>
/// <param name="Label">Display text shown on the button (for example, "Approve").</param>
/// <param name="Value">Machine-readable value emitted on click.</param>
/// <param name="RequiresComment">Whether the messenger should prompt for a free-text comment alongside this action.</param>
public sealed record HumanAction(
    string ActionId,
    string Label,
    string Value,
    bool RequiresComment);
