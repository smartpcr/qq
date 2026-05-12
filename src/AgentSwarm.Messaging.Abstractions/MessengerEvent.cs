namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Represents an inbound event from a messenger platform.
/// </summary>
public sealed record MessengerEvent
{
    public required string EventId { get; init; }

    public required EventType EventType { get; init; }

    public string? RawCommand { get; init; }

    public required string UserId { get; init; }

    public required string ChatId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string CorrelationId { get; init; }

    public string? Payload { get; init; }
}
