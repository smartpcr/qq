namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Deduplication store keyed by inbound <c>Activity.Id</c> (or <c>Activity.ReplyToId</c> for
/// invoke activities) used by <c>ActivityDeduplicationMiddleware</c> to suppress duplicate
/// webhook deliveries. Aligned with <c>architecture.md</c> §4.8.
/// </summary>
public interface IActivityIdStore
{
    /// <summary>
    /// Atomically record the supplied activity ID. Returns <see langword="true"/> when the ID
    /// has already been seen (the caller should treat the inbound activity as a duplicate and
    /// short-circuit), <see langword="false"/> when the ID was newly recorded.
    /// </summary>
    Task<bool> IsSeenOrMarkAsync(string activityId, CancellationToken ct);
}
