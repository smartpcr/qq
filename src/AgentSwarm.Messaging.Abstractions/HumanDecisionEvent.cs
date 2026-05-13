namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Records a human operator's decision in response to an agent question.
/// </summary>
public sealed record HumanDecisionEvent
{
    private readonly string _correlationId = null!;

    public required string QuestionId { get; init; }

    public required string ActionValue { get; init; }

    public string? Comment { get; init; }

    public required string Messenger { get; init; }

    public required string ExternalUserId { get; init; }

    public required string ExternalMessageId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// Trace identifier — must be non-null, non-empty, non-whitespace per
    /// the "All messages include trace/correlation ID" acceptance criterion.
    /// </summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }
}
