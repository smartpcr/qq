// -----------------------------------------------------------------------
// <copyright file="PersistentAuditLogReader.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Stage 5.3 iter-8 evaluator item 1 — default
/// <see cref="IAuditLogReader"/> implementation. Opens a fresh
/// service scope per call (mirroring the
/// <see cref="PersistentAuditLogger"/> writer) and projects the
/// underlying <see cref="AuditDbContext.AuditLogs"/>
/// <see cref="DbSet{TEntity}"/> with
/// <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}(IQueryable{TEntity})"/>
/// so a returned row cannot be flipped to a Modified state and
/// re-saved via the same context.
/// </summary>
/// <remarks>
/// All write surfaces on this type are intentionally absent — the
/// interface only exposes <c>Get*</c> methods, the
/// <see cref="DbSet{TEntity}"/> is <see langword="internal"/> on
/// <see cref="AuditDbContext"/>, and the
/// <see cref="AuditLogBulkMutationInterceptor"/> rejects every
/// UPDATE/DELETE/RAW-SQL targeting <c>audit_logs</c>, so a
/// hostile caller that resolves this type still cannot mutate the
/// table.
/// </remarks>
public sealed class PersistentAuditLogReader : IAuditLogReader
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PersistentAuditLogReader(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return Array.Empty<AuditLogEntry>();
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        return await db.AuditLogs
            .AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .OrderBy(x => x.Timestamp)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> GetByQuestionIdAsync(
        string questionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            return Array.Empty<AuditLogEntry>();
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        return await db.AuditLogs
            .AsNoTracking()
            .Where(x => x.QuestionId == questionId)
            .OrderBy(x => x.Timestamp)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
