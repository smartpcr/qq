namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Wraps an <see cref="AgentQuestion"/> with routing and context metadata.
/// The envelope is the unit of transport through IMessengerConnector.SendQuestionAsync.
/// </summary>
public sealed record AgentQuestionEnvelope
{
    public required AgentQuestion Question { get; init; }

    /// <summary>
    /// The ActionId from AllowedActions to apply automatically on timeout.
    /// When null, the question expires with ActionValue = "__timeout__".
    /// </summary>
    public string? ProposedDefaultActionId { get; init; }

    public IReadOnlyDictionary<string, string> RoutingMetadata { get; init; } =
        new Dictionary<string, string>();
}
