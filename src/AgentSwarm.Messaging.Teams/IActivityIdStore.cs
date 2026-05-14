namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Webhook-deduplication state store. Aligned with <c>architecture.md</c> §4.8.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="Middleware.ActivityDeduplicationMiddleware"/> to suppress duplicate
/// inbound Bot Framework webhook deliveries by <c>Activity.Id</c> (or
/// <c>Activity.ReplyToId</c> for invoke activities).
/// </para>
/// <para>
/// The default implementation registered in Stage 2.1 is
/// <see cref="InMemoryActivityIdStore"/>, which is sufficient for single-instance
/// deployments. Multi-instance deployments should swap in a Redis-backed store.
/// </para>
/// </remarks>
public interface IActivityIdStore
{
    /// <summary>
    /// Atomically check whether <paramref name="activityId"/> has been seen previously and,
    /// if not, mark it as seen with the configured TTL. The atomicity guarantee means two
    /// concurrent callers presenting the same ID can never both observe <c>false</c>.
    /// </summary>
    /// <param name="activityId">The Bot Framework activity ID to check / mark.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>
    /// <c>true</c> if the activity ID was already seen (duplicate); <c>false</c> if the call
    /// successfully marked it as seen for the first time.
    /// </returns>
    Task<bool> IsSeenOrMarkAsync(string activityId, CancellationToken ct);
}
