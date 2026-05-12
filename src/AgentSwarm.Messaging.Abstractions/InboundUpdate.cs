namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Deduplication and durable work-queue record for inbound Telegram updates.
/// Defined in Abstractions so IInboundUpdateStore can reference it without
/// depending on the Persistence project.
/// </summary>
public sealed record InboundUpdate
{
    /// <summary>Telegram's monotonic update_id. Primary key.</summary>
    public required long UpdateId { get; init; }

    /// <summary>Full serialized Telegram Update JSON.</summary>
    public required string RawPayload { get; init; }

    /// <summary>First receipt timestamp.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>When processing completed (null = in-flight).</summary>
    public DateTimeOffset? ProcessedAt { get; init; }

    /// <summary>Four-status model: Received, Processing, Completed, Failed.</summary>
    public required IdempotencyStatus IdempotencyStatus { get; init; }

    /// <summary>Incremented on each reprocessing attempt by InboundRecoverySweep.</summary>
    public int AttemptCount { get; init; }

    /// <summary>Stores the latest failure reason for diagnostics.</summary>
    public string? ErrorDetail { get; init; }
}
