// -----------------------------------------------------------------------
// <copyright file="PersistentTaskOversightRepository.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF Core-backed <see cref="ITaskOversightRepository"/>. Stores
/// task-to-operator oversight assignments in the
/// <c>task_oversights</c> SQLite table. Used by the Stage 3.2
/// <c>HandoffCommandHandler</c> to record handoffs and by the Stage
/// 2.7 swarm-event subscription service to route status / alert
/// events to the operator who currently oversees a given task.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime + scope.</b> Registered as a singleton so the singleton
/// command-handler registration in <c>TelegramServiceCollectionExtensions</c>
/// can depend on it without violating the captive-dependency rule;
/// each call opens a fresh
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScope"/>
/// to retrieve the scoped <see cref="MessagingDbContext"/>. Mirrors
/// <see cref="PersistentOutboundMessageIdIndex"/>.
/// </para>
/// <para>
/// <b>Upsert semantics.</b> <see cref="UpsertAsync"/> finds the row by
/// <see cref="TaskOversight.TaskId"/> and either inserts or updates in
/// a single <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
/// Replays of the same handoff are idempotent because (a) the PK
/// constrains there to be at most one row per task, and (b) the upsert
/// path always overwrites the same columns with the same values when
/// the handoff is logically identical.
/// </para>
/// </remarks>
public sealed class PersistentTaskOversightRepository : ITaskOversightRepository
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistentTaskOversightRepository> _logger;

    public PersistentTaskOversightRepository(
        IServiceScopeFactory scopeFactory,
        ILogger<PersistentTaskOversightRepository> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<TaskOversight?> GetByTaskIdAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        return await db.TaskOversights
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TaskId == taskId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(TaskOversight oversight, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(oversight);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var existing = await db.TaskOversights
            .FirstOrDefaultAsync(x => x.TaskId == oversight.TaskId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            db.TaskOversights.Add(oversight);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(oversight);
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
            && sqlite.SqliteErrorCode == 19)
        {
            // SQLite error 19 = constraint violation. A concurrent
            // upsert from another handoff with the same TaskId may
            // have raced us; the row exists now, so retry the find/
            // update path once. Mirrors the pattern in
            // PersistentOutboundMessageIdIndex.
            _logger.LogDebug(
                ex,
                "TaskOversight upsert for TaskId={TaskId} raced with a concurrent writer; retrying once.",
                oversight.TaskId);

            db.ChangeTracker.Clear();
            var raced = await db.TaskOversights
                .FirstOrDefaultAsync(x => x.TaskId == oversight.TaskId, ct)
                .ConfigureAwait(false);
            if (raced is null)
            {
                db.TaskOversights.Add(oversight);
            }
            else
            {
                db.Entry(raced).CurrentValues.SetValues(oversight);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskOversight>> GetByOperatorAsync(
        Guid operatorBindingId,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        return await db.TaskOversights
            .AsNoTracking()
            .Where(x => x.OperatorBindingId == operatorBindingId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
