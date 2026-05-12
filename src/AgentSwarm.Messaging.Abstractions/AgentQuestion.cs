namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Represents a blocking question from an agent to a human operator.
/// The shared model does not include a DefaultAction property;
/// the proposed default action is carried as sidecar metadata
/// via <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>.
/// </summary>
public sealed record AgentQuestion
{
    public required string QuestionId { get; init; }

    public required string AgentId { get; init; }

    public required string TaskId { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required MessageSeverity Severity { get; init; }

    public required IReadOnlyList<HumanAction> AllowedActions { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string CorrelationId { get; init; }
}
