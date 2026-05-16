namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Read-only query surface for compliance review of the append-only audit log
/// implemented by <see cref="IAuditLogger"/>. Aligned with <c>implementation-plan.md</c>
/// §5.2 step 6 ("Implement <c>AuditLogQueryService</c> with methods for compliance
/// review: <c>GetByDateRangeAsync</c>, <c>GetByActorAsync</c>,
/// <c>GetByCorrelationIdAsync</c>") and <c>tech-spec.md</c> §4.3 (Canonical Audit
/// Record Schema).
/// </summary>
/// <remarks>
/// <para>
/// All three queries return canonical <see cref="AuditEntry"/> records ordered by
/// <see cref="AuditEntry.Timestamp"/> ascending so the resulting sequence is a
/// chronological replay of the audited events. The interface intentionally exposes
/// <see cref="AuditEntry"/> rather than an EF entity so compliance tooling can consume
/// it without taking a dependency on the storage layer.
/// </para>
/// <para>
/// The interface is intentionally narrow — pagination, free-text search, and bulk
/// export are deferred to future stages. Callers expecting unbounded result sets
/// should pass a small date window or filter by correlation ID first.
/// </para>
/// </remarks>
public interface IAuditLogQueryService
{
    /// <summary>
    /// Return every audit entry whose <see cref="AuditEntry.Timestamp"/> falls within
    /// the inclusive <paramref name="fromUtc"/> / exclusive <paramref name="toUtc"/>
    /// window. Useful for compliance reviewers asking "what happened between 09:00
    /// and 10:00 yesterday".
    /// </summary>
    /// <param name="fromUtc">Inclusive lower bound (UTC).</param>
    /// <param name="toUtc">Exclusive upper bound (UTC); must be strictly greater than <paramref name="fromUtc"/>.</param>
    /// <param name="cancellationToken">Token observed by transport-layer I/O.</param>
    /// <returns>Entries in chronological order (oldest first).</returns>
    Task<IReadOnlyList<AuditEntry>> GetByDateRangeAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Return every audit entry whose <see cref="AuditEntry.ActorId"/> matches
    /// <paramref name="actorId"/> exactly. Used by compliance reviewers chasing all
    /// actions taken by a specific user (AAD object ID) or agent (agent ID).
    /// </summary>
    /// <param name="actorId">Exact-match actor identifier — the AAD object ID when the actor is a user, or the agent ID when the actor is an agent.</param>
    /// <param name="cancellationToken">Token observed by transport-layer I/O.</param>
    /// <returns>Entries in chronological order (oldest first).</returns>
    Task<IReadOnlyList<AuditEntry>> GetByActorAsync(
        string actorId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Return every audit entry whose <see cref="AuditEntry.CorrelationId"/> matches
    /// <paramref name="correlationId"/> exactly. This is the canonical compliance
    /// query: a single correlation ID strings together the full lifecycle (command →
    /// proactive notification → card action → reply) for an agent task.
    /// </summary>
    /// <param name="correlationId">End-to-end trace ID. Compared with ordinal equality.</param>
    /// <param name="cancellationToken">Token observed by transport-layer I/O.</param>
    /// <returns>Entries in chronological order (oldest first).</returns>
    Task<IReadOnlyList<AuditEntry>> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken);
}
