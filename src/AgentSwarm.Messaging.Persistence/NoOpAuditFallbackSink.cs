namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// No-op <see cref="IAuditFallbackSink"/> registered as the default sink so callers
/// can resolve <see cref="IAuditFallbackSink"/> without an explicit override. Every
/// call returns <see cref="Task.CompletedTask"/> without persisting the entry — this
/// is appropriate for unit tests and early-phase deployments where no durable
/// secondary is yet available.
/// </summary>
/// <remarks>
/// <para>
/// <b>Production deployments must NOT rely on this sink.</b> When this sink is the
/// effective registration and the primary <see cref="IAuditLogger"/> exhausts retries,
/// the audit row reaches no durable storage; the only remaining evidence is the
/// <c>FALLBACK_AUDIT_ENTRY</c> log line emitted by <c>CardActionHandler</c>, which
/// requires operator log-shipping to forward into the audit store. Register a durable
/// sink (<see cref="FileAuditFallbackSink"/> or a custom implementation) in production
/// to satisfy the enterprise compliance contract from <c>tech-spec.md</c> §4.3.
/// </para>
/// <para>
/// Unlike <see cref="NoOpAuditLogger"/>, this stub does not exist to defer behaviour
/// to a later stage — it exists so that callers wishing to opt out of the secondary
/// sink can do so explicitly without breaking the DI graph.
/// </para>
/// </remarks>
public sealed class NoOpAuditFallbackSink : IAuditFallbackSink
{
    /// <inheritdoc />
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        _ = entry;
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
