namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Discriminated-union base for inbound events delivered from the agent
/// swarm orchestrator to messenger connectors. Concrete subtypes are sealed
/// records below.
/// </summary>
public abstract record SwarmEvent
{
    private readonly string _correlationId = null!;

    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }
}

/// <summary>
/// Wraps an <see cref="AgentQuestionEnvelope"/> so that the connector renders
/// the question with inline-keyboard buttons.
/// </summary>
public sealed record AgentQuestionEvent : SwarmEvent
{
    public required AgentQuestionEnvelope Envelope { get; init; }
}

/// <summary>
/// Operator-facing alert raised by an agent (e.g. build failure, incident).
/// Contains the full ten-field set required for <c>TaskOversight</c>
/// routing and workspace fallback per architecture.md §5.6 and §4.6.
/// </summary>
public sealed record AgentAlertEvent : SwarmEvent
{
    public required string AlertId { get; init; }

    public required string AgentId { get; init; }

    /// <summary>
    /// The task the agent was executing when the alert fired. Always
    /// populated; used by <c>ITaskOversightRepository.GetByTaskIdAsync</c>
    /// to resolve the oversight operator for routing.
    /// </summary>
    public required string TaskId { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required MessageSeverity Severity { get; init; }

    public required string WorkspaceId { get; init; }

    public required string TenantId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Informational progress update emitted by an agent. Rendered as plain text
/// in the operator's chat.
/// </summary>
public sealed record AgentStatusUpdateEvent : SwarmEvent
{
    public required string AgentId { get; init; }

    public required string TaskId { get; init; }

    public required string StatusText { get; init; }
}
