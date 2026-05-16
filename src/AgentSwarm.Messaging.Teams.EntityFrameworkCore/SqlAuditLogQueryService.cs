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
/// </remarks>
public sealed class SqlAuditLogQueryService : IAuditLogQueryService
{
    private readonly IDbContextFactory<AuditLogDbContext> _contextFactory;

    /// <summary>
    /// Construct the query service with the DI-bound EF context factory.
    /// </summary>
    /// <param name="contextFactory">EF Core context factory bound by DI.</param>
    public SqlAuditLogQueryService(IDbContextFactory<AuditLogDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

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

        var entities = await ctx.AuditLog
            .AsNoTracking()
            .Where(e => e.Timestamp >= fromUtc && e.Timestamp < toUtc)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(Map).ToArray();
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
