namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Routing wrapper around an <see cref="AgentQuestion"/>. Keeps connector-specific
/// addressing out of the shared <see cref="AgentQuestion"/> model. For Discord,
/// <see cref="RoutingMetadata"/> carries <c>DiscordChannelId</c> and optionally
/// <c>DiscordThreadId</c>; other connectors populate their own keys.
/// </summary>
/// <param name="Question">The wrapped question payload.</param>
/// <param name="ProposedDefaultActionId">
/// Optional pointer into <see cref="AgentQuestion.AllowedActions"/> identifying the
/// action the agent would prefer the operator to take. Connectors may visually
/// highlight the matching button.
/// </param>
/// <param name="RoutingMetadata">
/// Connector-specific routing keys (e.g. <c>DiscordChannelId</c>,
/// <c>DiscordThreadId</c>, <c>SlackChannelId</c>). Keys are case-sensitive.
/// </param>
public sealed record AgentQuestionEnvelope(
    AgentQuestion Question,
    string? ProposedDefaultActionId,
    IReadOnlyDictionary<string, string> RoutingMetadata);
