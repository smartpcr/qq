namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Represents an inbound event from a messenger platform.
/// </summary>
public sealed record MessengerEvent
{
    private readonly string _correlationId = null!;

    public required string EventId { get; init; }

    public required EventType EventType { get; init; }

    public string? RawCommand { get; init; }

    public required string UserId { get; init; }

    public required string ChatId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Trace identifier — must be non-null, non-empty, non-whitespace per
    /// the "All messages include trace/correlation ID" acceptance criterion.
    /// </summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }

    public string? Payload { get; init; }
}
