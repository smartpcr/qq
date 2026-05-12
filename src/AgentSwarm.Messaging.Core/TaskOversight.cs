namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Tracks which operator currently has oversight of which task.
/// Defined in Core so ITaskOversightRepository can reference it
/// without depending on Persistence.
/// </summary>
public sealed record TaskOversight
{
    /// <summary>Primary key. The task being overseen.</summary>
    public required string TaskId { get; init; }

    /// <summary>FK to OperatorBinding.Id. The operator who currently has oversight.</summary>
    public required Guid OperatorBindingId { get; init; }

    /// <summary>When oversight was assigned or last transferred.</summary>
    public required DateTimeOffset AssignedAt { get; init; }

    /// <summary>The operator who initiated the handoff.</summary>
    public required string AssignedBy { get; init; }

    /// <summary>Trace ID for the handoff action.</summary>
    public required string CorrelationId { get; init; }
}
