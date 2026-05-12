namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Represents a single action button an agent question can offer to a human operator.
/// </summary>
public sealed record HumanAction
{
    public required string ActionId { get; init; }

    public required string Label { get; init; }

    public required string Value { get; init; }

    public bool RequiresComment { get; init; }
}
