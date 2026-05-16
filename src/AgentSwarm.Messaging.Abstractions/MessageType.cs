namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Classification of a <see cref="MessengerMessage"/> payload. Connectors
/// use this to drive presentation: emoji prefix, color bar, threading rules,
/// etc. The Slack story (Stage 4 -- <c>SlackMessageRenderer</c>) styles
/// content by these values.
/// </summary>
/// <remarks>
/// COMPILE STUB. Owned by the upstream Abstractions story. The values here
/// are the minimum set referenced in
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (Stage 4: "styled by <c>MessageType</c> (status update, completion,
/// error)") so the Slack connector compiles. The canonical enum may add
/// additional members later.
/// </remarks>
public enum MessageType
{
    /// <summary>
    /// Default value reserved for an uninitialised message. Connectors
    /// should treat this as a programming error.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// In-flight progress / status update from an agent.
    /// </summary>
    StatusUpdate = 1,

    /// <summary>
    /// Final successful outcome of a task.
    /// </summary>
    Completion = 2,

    /// <summary>
    /// Failure, exception, or other error condition.
    /// </summary>
    Error = 3,
}
