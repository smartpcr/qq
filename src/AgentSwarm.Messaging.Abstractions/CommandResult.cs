namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Structured outcome of a command dispatched through <see cref="ICommandRouter"/>.
/// </summary>
public sealed record CommandResult
{
    private readonly string _correlationId = null!;

    /// <summary>True when the command completed without error.</summary>
    public required bool Success { get; init; }

    /// <summary>Optional reply text to surface to the operator.</summary>
    public string? ResponseText { get; init; }

    /// <summary>Trace identifier propagated from the inbound event.</summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }

    /// <summary>
    /// Machine-readable error code when <see cref="Success"/> is <c>false</c>;
    /// <c>null</c> on successful completion.
    /// </summary>
    public string? ErrorCode { get; init; }
}
