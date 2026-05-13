namespace Qq.Messaging.Abstractions;

/// <summary>
/// Result of an outbound message delivery attempt.
/// </summary>
public sealed record DeliveryResult(
    string MessageId,
    DeliveryStatus Status,
    int AttemptCount,
    string? LastError,
    DateTimeOffset? DeliveredAtUtc);
