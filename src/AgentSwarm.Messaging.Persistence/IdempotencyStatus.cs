namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Lifecycle of a <see cref="DiscordInteractionRecord"/> as it moves through
/// the durable inbox / dedup queue. See architecture.md Section 3.1
/// (DiscordInteractionRecord) and Section 4.8 (IDiscordInteractionStore).
/// </summary>
public enum IdempotencyStatus
{
    /// <summary>
    /// Just persisted by the gateway; not yet picked up by the processing
    /// pipeline. Records in this state are eligible for recovery on restart.
    /// </summary>
    Received = 0,

    /// <summary>
    /// Pipeline has begun processing the interaction. Records in this state
    /// are also eligible for recovery on restart (the previous run crashed
    /// mid-flight).
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Pipeline finished processing successfully; <c>ProcessedAt</c> is set.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Pipeline reported a terminal failure for this attempt. Eligible for
    /// retry while <c>AttemptCount</c> remains below the configured cap.
    /// </summary>
    Failed = 3,
}
