namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Canonical severity vocabulary used across all messaging stages.
/// Determines outbound priority queue ordering.
/// </summary>
public enum MessageSeverity
{
    Critical,
    High,
    Normal,
    Low
}
