namespace Qq.Messaging.Abstractions;

/// <summary>
/// A lease token acquired when dequeuing a message for processing.
/// Must be acknowledged or released within the lease period.
/// </summary>
public sealed record QueueLease(
    string LeaseToken,
    MessageEnvelope Envelope,
    DateTimeOffset ExpiresAtUtc);
