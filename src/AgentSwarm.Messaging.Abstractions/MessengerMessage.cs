namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Represents a message flowing through the messenger system.
/// Throws <see cref="ArgumentNullException"/> at construction time if
/// <see cref="CorrelationId"/> is null.
/// </summary>
public sealed record MessengerMessage
{
    private readonly string _correlationId = null!;

    public required string MessageId { get; init; }

    /// <summary>
    /// Trace/correlation identifier. Throws <see cref="ArgumentNullException"/>
    /// when set to <c>null</c>; throws <see cref="ArgumentException"/> when
    /// empty or whitespace per the "All messages include trace/correlation
    /// ID" acceptance criterion.
    /// </summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }

    public string? AgentId { get; init; }

    public string? TaskId { get; init; }

    /// <summary>
    /// Conversation thread identifier. Required per architecture.md §3.1 / HC-9.
    /// </summary>
    public required string ConversationId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Text { get; init; }

    public required MessageSeverity Severity { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}
