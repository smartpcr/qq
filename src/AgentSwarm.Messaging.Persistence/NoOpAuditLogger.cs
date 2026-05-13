namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// No-op <see cref="IAuditLogger"/> stub used as the default DI registration until
/// <c>SqlAuditLogger</c> lands in Stage 5.2. Every call returns
/// <see cref="Task.CompletedTask"/> without performing I/O, validation, or argument
/// inspection — per the Stage 1.3 brief, this stub "accepts all <c>LogAsync</c> calls as
/// no-ops".
/// </summary>
/// <remarks>
/// <para>
/// This stub is created in Stage 1.3 (alongside <see cref="IAuditLogger"/>) so it is
/// available when Stage 2.1 registers <see cref="IAuditLogger"/> in DI.
/// <c>TeamsSwarmActivityHandler</c> (Stage 2.2) injects <see cref="IAuditLogger"/> as a
/// required dependency — without this stub, the handler cannot be constructed before the
/// concrete <c>SqlAuditLogger</c> ships. Stage 5.2 replaces this stub via DI override.
/// </para>
/// <para>
/// The contract is intentionally permissive — null entries and canceled tokens do NOT
/// throw. Concrete loggers (Stage 5.2's <c>SqlAuditLogger</c>) are free to add argument
/// validation and cancellation observation; callers must not rely on the stub to surface
/// those faults.
/// </para>
/// </remarks>
public sealed class NoOpAuditLogger : IAuditLogger
{
    /// <inheritdoc />
    public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        // Intentionally a true no-op: no argument validation, no cancellation observation.
        // The stub exists solely to satisfy DI before SqlAuditLogger ships in Stage 5.2.
        _ = entry;
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
