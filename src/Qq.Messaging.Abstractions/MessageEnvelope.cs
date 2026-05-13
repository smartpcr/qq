namespace Qq.Messaging.Abstractions;

/// <summary>
/// Wraps an outbound message for durable queuing with retry metadata.
/// </summary>
public sealed record MessageEnvelope
{
    public required string EnvelopeId { get; init; }
    public required string DeduplicationKey { get; init; }
    public required OutboundMessage Payload { get; init; }
    public required CorrelationContext Correlation { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; init; }
    public DeliveryStatus Status { get; init; } = DeliveryStatus.Queued;
    public DateTimeOffset? NextVisibleAtUtc { get; init; }
}
