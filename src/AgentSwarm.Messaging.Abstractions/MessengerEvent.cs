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

    /// <summary>
    /// Raw chat-type token surfaced by the source transport. For
    /// Telegram this is the lowercase string from
    /// <c>Update.Message.Chat.Type</c> (<c>"private"</c>,
    /// <c>"group"</c>, <c>"supergroup"</c>, <c>"channel"</c>); other
    /// connectors may surface their platform-native equivalent.
    /// </summary>
    /// <remarks>
    /// Stage 3.4 — the <c>/start</c> onboarding path in
    /// <c>TelegramUserAuthorizationService</c> uses this value to
    /// populate <c>OperatorBinding.ChatType</c> instead of defaulting
    /// every binding to <c>Private</c>. Kept as a nullable
    /// <see cref="string"/> (not an enum) because
    /// <c>AgentSwarm.Messaging.Abstractions</c> does NOT reference
    /// <c>AgentSwarm.Messaging.Core</c> where <c>ChatType</c> lives,
    /// and adding a Core-typed property here would invert the layering.
    /// Consumers that need the enum (currently only the authorization
    /// service) parse it locally via the established
    /// <c>TelegramChatTypeParser</c> helper.
    /// </remarks>
    public string? ChatType { get; init; }

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
