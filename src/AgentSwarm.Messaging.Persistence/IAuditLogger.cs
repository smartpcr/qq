namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Append-only audit-log writer for command-receipt, card-action, proactive-notification,
/// and security-rejection events. Concrete implementations persist the entry durably (see
/// <c>SqlAuditLogger</c> in Stage 5.2). For early phases the
/// <see cref="NoOpAuditLogger"/> stub satisfies the dependency so consumers
/// (<c>TeamsSwarmActivityHandler</c>) can be constructed before the SQL implementation
/// lands.
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Persist a single audit entry. The implementation is expected to be append-only —
    /// callers must not rely on updates or deletes through this interface.
    /// </summary>
    /// <param name="entry">The fully-populated, immutable audit entry to persist.</param>
    /// <param name="cancellationToken">Token observed by transport-layer I/O.</param>
    Task LogAsync(AuditEntry entry, CancellationToken cancellationToken);
}
