namespace Qq.Messaging.Abstractions;

/// <summary>
/// Prevents duplicate processing of the same inbound platform event.
/// Keyed on the platform update/event identifier, not the message ID.
/// </summary>
public interface IMessageDeduplicator
{
    /// <summary>
    /// Atomically mark an event as processed.
    /// Returns true if this is the first time; false if already seen.
    /// </summary>
    Task<bool> TryMarkProcessedAsync(
        string platformUpdateId,
        CancellationToken cancellationToken = default);
}
