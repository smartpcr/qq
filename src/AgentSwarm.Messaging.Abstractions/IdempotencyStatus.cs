namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Tracks the processing state of an inbound update for deduplication.
/// Matches architecture.md §3.1 four-status model exactly.
/// </summary>
public enum IdempotencyStatus
{
    Received,
    Processing,
    Completed,
    Failed
}
