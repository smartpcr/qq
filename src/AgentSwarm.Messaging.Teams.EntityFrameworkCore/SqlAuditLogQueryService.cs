using AgentSwarm.Messaging.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IAuditLogQueryService"/>. Serves the three
/// canonical compliance-review queries (date range, actor, correlation ID) against
/// the <c>AuditLog</c> table per <c>implementation-plan.md</c> §5.2 step 6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only projection.</b> Every query uses <c>AsNoTracking()</c> so the EF
/// change tracker never stamps rows for save and the returned entities cannot be
/// round-tripped through <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
/// In combination with the database-level immutability triggers this provides
/// defense in depth: even a caller that mutates an in-memory property cannot push
/// the change back to storage.
/// </para>
/// <para>
/// <b>Deterministic ordering.</b> Every query orders by
/// <see cref="AuditLogEntity.Timestamp"/> ascending then by
/// <see cref="AuditLogEntity.Id"/> so consumers see a temporally coherent replay
/// with a stable tie-breaker across calls.
/// </para>
/// <para>
/// <b>Checksum preservation.</b> The mapper copies the stored
/// <see cref="AuditLogEntity.Checksum"/> straight through to
/// <see cref="AuditEntry.Checksum"/> rather than recomputing it, so reviewers can
/// re-run <see cref="AuditEntry.ComputeChecksum"/> on the returned record and
/// compare against the stored value to verify tamper detection end-to-end.
/// </para>
/// <para>
/// <b>Server-side safety cap (reviewer feedback).</b> A compliance audit table is
/// expected to grow without bound, and the <see cref="IAuditLogQueryService"/>
/// contract defers pagination to a later stage. To keep that deferral safe, every
/// query method here caps the materialized result set at <see cref="MaxRows"/>
/// (default <see cref="DefaultMaxRows"/>) using a <c>Take(MaxRows + 1)</c>
/// <i>overflow canary</i>. If the canary row materializes the service throws
/// <see cref="InvalidOperationException"/> rather than silently truncating —
/// silent truncation is unacceptable for compliance review because the caller
/// would believe they had the full picture while a critical entry was hidden.
/// Hosts that legitimately need a larger ceiling (e.g. an archival export job)
/// construct the service via the two-argument constructor with an explicit
/// <c>maxRows</c> value.
/// </para>
/// </remarks>
public sealed class SqlAuditLogQueryService : IAuditLogQueryService
{
    /// <summary>
    /// Default ceiling on rows materialized by a single query (10,000).
    /// <list type="bullet">
    ///   <item><description>An <see cref="AuditEntry"/> projection is on the order
    ///   of 1–2&#160;KB once strings settle, so 10,000 rows fits in roughly
    ///   10–20&#160;MB — comfortably inside a typical compliance-review session's
    ///   working set.</description></item>
    ///   <item><description>A single actor, correlation ID, or sensibly-scoped
    ///   date window producing more than 10,000 audit entries almost always
    ///   means the reviewer wants a narrower filter; throwing surfaces that
    ///   signal explicitly instead of letting the host slowly fall over.</description></item>
    /// </list>
    /// </summary>
    public const int DefaultMaxRows = 10_000;

    private readonly IDbContextFactory<AuditLogDbContext> _contextFactory;
    private readonly int _maxRows;

    /// <summary>
    /// DI-friendly constructor. Uses <see cref="DefaultMaxRows"/> as the safety
    /// cap. This is the signature wired by
    /// <c>EntityFrameworkCoreServiceCollectionExtensions.AddSqlAuditLogger</c>
    /// (<c>TryAddSingleton&lt;SqlAuditLogQueryService&gt;()</c>); preserving it
    /// keeps the existing registration unchanged.
    /// </summary>
    /// <param name="contextFactory">EF Core context factory bound by DI.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contextFactory"/> is <see langword="null"/>.</exception>
    public SqlAuditLogQueryService(IDbContextFactory<AuditLogDbContext> contextFactory)
        : this(contextFactory, DefaultMaxRows)
    {
    }

    /// <summary>
    /// Explicit-cap constructor. Use this overload to register the service with a
    /// non-default ceiling (for example an archival export host that intentionally
    /// materializes a larger window).
    /// </summary>
    /// <param name="contextFactory">EF Core context factory bound by DI.</param>
    /// <param name="maxRows">Strictly positive ceiling on rows returned by any
    /// single query. When a query matches more than this many rows the service
    /// throws <see cref="InvalidOperationException"/> instead of truncating.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contextFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRows"/> is less than or equal to zero.</exception>
    public SqlAuditLogQueryService(
        IDbContextFactory<AuditLogDbContext> contextFactory,
        int maxRows)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

        if (maxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRows),
                maxRows,
                "maxRows must be strictly positive — the audit query service refuses to be configured with a non-positive row ceiling.");
        }

        _maxRows = maxRows;
    }

    /// <summary>
    /// Effective row ceiling applied by every query method on this instance.
    /// </summary>
    public int MaxRows => _maxRows;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> GetByDateRangeAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException(
                $"toUtc ({toUtc:o}) must be strictly greater than fromUtc ({fromUtc:o}).",
                nameof(toUtc));
        }

        await using var ctx = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Take MaxRows + 1 so we can detect overflow without materializing the
        // unbounded result set. The extra row is the overflow canary: if it
        // appears, the query matched too many rows and we fail-fast rather than
        // silently truncating.
        var entities = await ctx.AuditLog
            .AsNoTracking()
            .Where(e => e.Timestamp >= fromUtc && e.Timestamp < toUtc)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .Take(_maxRows + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        ThrowIfCapExceeded(
            entities.Count,
            queryDescription: $"GetByDateRangeAsync([{fromUtc:o}, {toUtc:o}))",
            remediation: "narrow the date window, or filter by correlation ID via GetByCorrelationIdAsync");

        return entities.Select(Map).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> GetByActorAsync(
        string actorId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("actorId must be a non-empty string.", nameof(actorId));
        }

        await using var ctx = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var entities = await ctx.AuditLog
            .AsNoTracking()
            .Where(e => e.ActorId == actorId)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .Take(_maxRows + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        ThrowIfCapExceeded(
            entities.Count,
            queryDescription: $"GetByActorAsync(actorId='{actorId}')",
            remediation: "combine the actor filter with a bounded date window once pagination ships, or filter by correlation ID for the specific trace under review");

        return entities.Select(Map).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("correlationId must be a non-empty string.", nameof(correlationId));
        }

        await using var ctx = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var entities = await ctx.AuditLog
            .AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .Take(_maxRows + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A correlation ID is the narrowest possible filter (one end-to-end task
        // lifecycle) so overflow here almost always means an upstream producer is
        // reusing a correlation ID across unrelated tasks. The exception message
        // calls that out directly.
        ThrowIfCapExceeded(
            entities.Count,
            queryDescription: $"GetByCorrelationIdAsync(correlationId='{correlationId}')",
            remediation: "this almost certainly indicates a correlation-ID reuse bug upstream — a single end-to-end trace should not produce more than the configured ceiling of audit entries. Inspect the producer that emitted this correlation ID");

        return entities.Select(Map).ToArray();
    }

    private void ThrowIfCapExceeded(int materializedCount, string queryDescription, string remediation)
    {
        if (materializedCount > _maxRows)
        {
            throw new InvalidOperationException(
                $"{queryDescription} matched more than the configured safety cap of {_maxRows} rows " +
                $"(materialized {materializedCount} rows including the overflow canary). The audit " +
                $"query service refuses to return a partially-truncated result set because compliance " +
                $"review depends on completeness. Pagination is deferred on IAuditLogQueryService — to " +
                $"proceed, {remediation}. Hosts that genuinely need a larger ceiling can construct " +
                $"{nameof(SqlAuditLogQueryService)} via its two-argument constructor with an explicit " +
                $"maxRows value.");
        }
    }

    private static AuditEntry Map(AuditLogEntity e) => new()
    {
        Timestamp = e.Timestamp,
        CorrelationId = e.CorrelationId,
        EventType = e.EventType,
        ActorId = e.ActorId,
        ActorType = e.ActorType,
        TenantId = e.TenantId,
        AgentId = e.AgentId,
        TaskId = e.TaskId,
        ConversationId = e.ConversationId,
        Action = e.Action,
        PayloadJson = e.PayloadJson,
        Outcome = e.Outcome,
        Checksum = e.Checksum,
    };
}
