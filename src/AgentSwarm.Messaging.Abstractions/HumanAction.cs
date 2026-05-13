using System.ComponentModel.DataAnnotations;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// An action a human may take in response to an <see cref="AgentQuestion"/>.
/// Rendered as a button on the proactive Adaptive Card by the Teams connector.
/// </summary>
public sealed record HumanAction
{
    /// <summary>Stable id used by the connector to round-trip the action.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ActionId { get; init; } = string.Empty;

    /// <summary>Human-readable button label.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Machine value sent back in <see cref="HumanDecisionEvent.ActionValue"/>
    /// when the human selects this action.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// When <c>true</c>, the messenger MUST collect a free-text comment before
    /// submitting the decision (e.g. "Reject" usually requires a reason).
    /// </summary>
    public bool RequiresComment { get; init; }
}
