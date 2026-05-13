using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Base type for inbound messenger events flowing from a connector to the
/// orchestrator. The <see cref="EventType"/> discriminator is drawn from the
/// canonical <see cref="MessengerEventTypes"/> vocabulary; <see cref="Source"/>
/// from <see cref="MessengerEventSources"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CommandEvent), nameof(CommandEvent))]
[JsonDerivedType(typeof(DecisionEvent), nameof(DecisionEvent))]
[JsonDerivedType(typeof(TextEvent), nameof(TextEvent))]
public abstract record MessengerEvent
{
    /// <summary>Discriminator value from <see cref="MessengerEventTypes"/>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string EventType { get; init; } = string.Empty;

    /// <summary>Origination channel from <see cref="MessengerEventSources"/>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Source { get; init; } = string.Empty;
}

/// <summary>
/// A parsed slash- or natural-language command from a messenger. The
/// <see cref="MessengerEvent.EventType"/> varies based on the parsed verb —
/// e.g. <see cref="MessengerEventTypes.AgentTaskRequest"/> for <c>agent ask</c>,
/// <see cref="MessengerEventTypes.Escalation"/> / <see cref="MessengerEventTypes.PauseAgent"/>
/// / <see cref="MessengerEventTypes.ResumeAgent"/> for lifecycle commands,
/// <see cref="MessengerEventTypes.Command"/> for everything else.
/// </summary>
public sealed record CommandEvent : MessengerEvent
{
    /// <summary>Verb portion of the parsed command (e.g. <c>ask</c>, <c>pause</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Command { get; init; } = string.Empty;

    /// <summary>Positional / named arguments parsed from the command text.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
}

/// <summary>
/// An event wrapping a <see cref="HumanDecisionEvent"/> payload. Always carries
/// <see cref="MessengerEvent.EventType"/> = <see cref="MessengerEventTypes.Decision"/>.
/// </summary>
public sealed record DecisionEvent : MessengerEvent
{
    public DecisionEvent()
    {
        EventType = MessengerEventTypes.Decision;
    }

    [Required]
    public HumanDecisionEvent Decision { get; init; } = new();
}

/// <summary>
/// A free-text message event with no parsed command structure. Always carries
/// <see cref="MessengerEvent.EventType"/> = <see cref="MessengerEventTypes.Text"/>.
/// </summary>
public sealed record TextEvent : MessengerEvent
{
    public TextEvent()
    {
        EventType = MessengerEventTypes.Text;
    }

    [Required(AllowEmptyStrings = false)]
    public string Text { get; init; } = string.Empty;
}
