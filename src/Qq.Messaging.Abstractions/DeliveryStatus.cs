namespace Qq.Messaging.Abstractions;

/// <summary>
/// Tracks the lifecycle of an outbound message through the queue.
/// </summary>
public enum DeliveryStatus
{
    Queued = 0,
    Leased,
    Sent,
    Failed,
    DeadLettered
}
