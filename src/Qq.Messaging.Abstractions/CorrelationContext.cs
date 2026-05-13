namespace Qq.Messaging.Abstractions;

/// <summary>
/// Distributed tracing context propagated through every message.
/// </summary>
public sealed record CorrelationContext
{
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
    public string? CausationId { get; init; }
    public string? TraceId { get; init; }

    /// <summary>Create a child context that chains the current correlation as causation.</summary>
    public CorrelationContext CreateChild() =>
        new()
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            CausationId = CorrelationId,
            TraceId = TraceId
        };
}
