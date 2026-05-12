namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Records a human operator's decision in response to an agent question.
/// </summary>
public sealed record HumanDecisionEvent
{
    public required string QuestionId { get; init; }

    public required string ActionValue { get; init; }

    public string? Comment { get; init; }

    public required string Messenger { get; init; }

    public required string ExternalUserId { get; init; }

    public required string ExternalMessageId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public required string CorrelationId { get; init; }
}
