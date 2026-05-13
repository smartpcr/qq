namespace AgentSwarm.Messaging.Core;

using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Tracks which operator currently has oversight of which task.
/// Defined in Core so ITaskOversightRepository can reference it
/// without depending on Persistence.
/// </summary>
/// <remarks>
/// <see cref="CorrelationId"/> is guarded by
/// <see cref="CorrelationIdValidation.Require"/> at construction time so
/// an oversight handoff row cannot be persisted without a trace id — the
/// "All messages include trace/correlation ID" acceptance criterion
/// applies to every persisted oversight transition since handoffs span
/// multiple operators and the trace id is the only way to correlate
/// the handoff back to the originating command.
/// </remarks>
public sealed record TaskOversight
{
    private readonly string _correlationId = null!;

    /// <summary>Primary key. The task being overseen.</summary>
    public required string TaskId { get; init; }

    /// <summary>FK to OperatorBinding.Id. The operator who currently has oversight.</summary>
    public required Guid OperatorBindingId { get; init; }

    /// <summary>When oversight was assigned or last transferred.</summary>
    public required DateTimeOffset AssignedAt { get; init; }

    /// <summary>The operator who initiated the handoff.</summary>
    public required string AssignedBy { get; init; }

    /// <summary>
    /// Trace ID for the handoff action — must be non-null, non-empty,
    /// non-whitespace. Validated via
    /// <see cref="CorrelationIdValidation.Require"/> at construction time.
    /// </summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }
}
