namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Durable secondary persistence surface for <see cref="AuditEntry"/> records that the
/// primary <see cref="IAuditLogger"/> cannot accept. The sink exists so that a transient
/// or persistent outage of the primary audit store does not violate the enterprise
/// compliance contract that demands an <i>immutable</i> audit trail for every Adaptive
/// Card action (see <c>tech-spec.md</c> §4.3 and the Story Microsoft Teams Messenger
/// Support — "Persist immutable audit trail suitable for enterprise review").
/// </summary>
/// <remarks>
/// <para>
/// Concrete implementations MUST be:
/// <list type="bullet">
/// <item><description><b>Append-only.</b> Existing entries must not be overwritten or
/// deleted by <see cref="WriteAsync"/>. The same row may be written more than once if
/// the operator's replay daemon retries; downstream deduplication relies on the
/// <see cref="AuditEntry.Checksum"/> digest, not on the sink's transactional
/// semantics.</description></item>
/// <item><description><b>Durable.</b> A successful return from
/// <see cref="WriteAsync"/> must imply the bytes have reached a storage surface
/// that survives process restart (file system, append blob, queue with persistent
/// backing, etc.).</description></item>
/// <item><description><b>Independent of the primary audit store.</b> The whole point
/// of the sink is that it serves as a recovery surface when the primary
/// <see cref="IAuditLogger"/> is unavailable; if the sink shares infrastructure
/// with the primary, the durability claim collapses.</description></item>
/// </list>
/// </para>
/// <para>
/// The reference implementation <see cref="FileAuditFallbackSink"/> writes one
/// JSON-encoded entry per line to a local append-only file; the operator's log shipping
/// pipeline (or a dedicated replay daemon) is expected to forward the file content
/// into the primary audit store once it recovers. For tests and early-phase
/// deployments where no durable secondary is available, <see cref="NoOpAuditFallbackSink"/>
/// satisfies the dependency without performing I/O — but production deployments are
/// expected to register a durable sink.
/// </para>
/// </remarks>
public interface IAuditFallbackSink
{
    /// <summary>
    /// Persist the supplied audit entry to the durable secondary surface. Implementations
    /// must be append-only — never overwrite or mutate a previously-written entry.
    /// </summary>
    /// <param name="entry">The fully-populated, immutable audit entry to persist.</param>
    /// <param name="cancellationToken">Token observed by transport-layer I/O.</param>
    /// <returns>A task that completes when the entry has reached durable storage.</returns>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}
