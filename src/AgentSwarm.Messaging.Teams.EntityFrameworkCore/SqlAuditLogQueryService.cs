using AgentSwarm.Messaging.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IAuditLogQueryService"/>. Provides the three
/// canonical compliance-review queries (date range, actor, correlation ID) against the
/// <c>AuditLog</c> table per <c>implementation-plan.md</c> §5.2 step 6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only</b>: every query uses <c>AsNoTracking()</c> so the EF change tracker
/// does not stamp rows for save and the resulting entities cannot accidentally be
/// passed back through <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
/// Combined with the database-level immutability triggers this provides defense in
/// depth: even if a caller flips an entity property in memory, no mutation reaches
/// storage.
/// </para>
/// <para>
/// <b>Chronological order</b>: every query orders by
/// <see cref="AuditLogEntity.Timestamp"/> ascending so consumers receive a temporally
/// coherent replay of events. Ties on identical timestamps fall back to
/// <see cref="AuditLogEntity.Id"/> to keep the order deterministic across calls.
/// </para>
/// <para>
/// <b>Checksum preservation</b>: the mapper projects the stored
/// <see cref="AuditLogEntity.Checksum"/> verbatim onto the returned
/// <see cref="AuditEntry.Checksum"/> field rather than recomputing it. Compliance
/// reviewers can re-verify tamper detection by re-calling
/// <see cref="AuditEntry.ComputeChecksum"/> over the returned canonical fields and
/// comparing against the stored value.
/// </para>
/// <para>
/// <b>Server-side safety cap (reviewer feedback).</b> The <see cref="IAuditLogQueryService"/>
/// contract defers pagination, free-text search, and bulk export to a future stage —
/// but a compliance audit table grows without bound, so leaving the three queries
/// unbounded would let a broad date window or a high-volume actor query materialize
/// millions of rows and OOM the host. To make that deferral safe every query method
/// here applies a configurable hard ceiling (<see cref="MaxRows"/>, default
/// <see cref="DefaultMaxRows"/>) before <c>ToListAsync</c> using a
/// <c>Take(MaxRows + 1)</c> <i>overflow canary</i>. If the canary row materializes
/// the service throws <see cref="InvalidOperationException"/> rather than silently
/// truncating: silent truncation would be catastrophic for compliance review because
/// the caller would believe they had the full picture while a critical entry was
/// hidden. Hosts that legitimately need a larger ceiling (for example an archival
/// export job) construct the service via the two-argument constructor with an
/// explicit <c>maxRows</c> value.
/// </para>
/// </remarks>
public sealed class SqlAuditLogQueryService : IAuditLogQueryService
{
    /// <summary>
    /// Default ceiling on rows materialized by a single query (10,000). The figure
    /// balances two competing concerns:
    /// <list type="bullet">
    ///   <item><description>An <see cref="AuditEntry"/> projection is on the order of
    ///   1–2&#160;KB once strings settle, so 10,000 rows fits in roughly
    ///   10–20&#160;MB — comfortably inside a typical compliance-review session's
    ///   working set.</description></item>
    ///   <item><description>A single actor, correlation ID, or sensibly-scoped date
    ///   window producing more than 10,000 audit entries almost always means the
    ///   reviewer wants a narrower filter. Throwing surfaces that signal explicitly
    ///   instead of letting the host slowly fall over.</description></item>
    /// </list>
    /// </summary>
    public const int DefaultMaxRows = 10_000;

    private readonly IDbContextFactory<AuditLogDbContext> _contextFactory;
    private readonly int _maxRows;

    /// <summary>
    /// DI-friendly constructor that uses <see cref="DefaultMaxRows"/> as the safety
    /// cap. This is the signature wired by
    /// <c>EntityFrameworkCoreServiceCollectionExtensions.AddSqlAuditLogger</c>
    /// (<c>TryAddSingleton&lt;SqlAuditLogQueryService&gt;()</c>); preserving it keeps
    /// the existing registration and the existing
    /// <c>AuditLogStoreFixture</c> test wiring unchanged.
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
    /// <param name="maxRows">
    /// Strictly positive ceiling on rows returned by any single query. When a query
    /// matches more than this many rows the service throws
    /// <see cref="InvalidOperationException"/> rather than truncate. Must be strictly
    /// less than <see cref="int.MaxValue"/> so the <c>maxRows + 1</c> overflow canary
    /// applied internally cannot wrap to a negative <c>Take</c> argument.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="contextFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxRows"/> is less than or equal to zero, or equals
    /// <see cref="int.MaxValue"/>.
    /// </exception>
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
                "maxRows must be strictly positive — the audit query service refuses to be configured with a zero or negative row ceiling.");
        }

        // Reject int.MaxValue so the canary expression `_maxRows + 1` (used below in
        // every query method) cannot silently overflow into a negative Take argument
        // and degrade into an unbounded materialization.
        if (maxRows == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRows),
                maxRows,
                "maxRows must be strictly less than int.MaxValue so the overflow canary (maxRows + 1) does not wrap.");
        }

        _maxRows = maxRows;
    }

    /// <summary>
    /// Effective row ceiling applied by every query method on this instance.
    /// </summary>
    public int MaxRows => _maxRows;

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="toUtc"/> is less than or equal to <paramref name="fromUtc"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The query matched more than <see cref="MaxRows"/> rows. The service refuses to
    /// return a silently-truncated result set; narrow the date window or filter by
    /// correlation ID instead.
    /// </exception>
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

        // Take MaxRows + 1 so overflow is detectable without materializing the full
        // unbounded result set. The +1 row is the overflow canary: if it shows up,
        // the query matched too much and we fail-fast instead of silently truncating.
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
    /// <exception cref="ArgumentException"><paramref name="actorId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// The query matched more than <see cref="MaxRows"/> rows. The service refuses to
    /// return a silently-truncated result set; intersect the actor filter with a
    /// bounded date window or filter by correlation ID instead.
    /// </exception>
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
            remediation: "intersect the actor filter with a bounded date window once pagination ships, or filter by correlation ID for the specific trace under review");

        return entities.Select(Map).ToArray();
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="correlationId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// The query matched more than <see cref="MaxRows"/> rows. A single end-to-end
    /// trace should not produce that many audit entries; this almost always indicates
    /// a correlation-ID reuse bug in an upstream producer.
    /// </exception>
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

        // A correlation ID is the narrowest possible filter (one task's lifecycle),
        // so overflow here almost always means an upstream producer is reusing a
        // correlation ID across unrelated tasks. The remediation message calls that
        // out directly rather than suggesting a date-window workaround that would
        // mask the underlying bug.
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
