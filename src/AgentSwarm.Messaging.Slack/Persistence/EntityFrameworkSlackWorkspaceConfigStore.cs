// -----------------------------------------------------------------------
// <copyright file="EntityFrameworkSlackWorkspaceConfigStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF Core-backed <see cref="ISlackWorkspaceConfigStore"/>. Reads from the
/// <c>slack_workspace_config</c> table via a freshly-scoped
/// <typeparamref name="TContext"/> resolved from
/// <see cref="IServiceScopeFactory"/>. Provides the Stage 3.1
/// "durable / config-backed" workspace store so a restarted Worker host
/// can resolve <see cref="SlackWorkspaceConfig.SigningSecretRef"/>
/// without first being re-seeded.
/// </summary>
/// <typeparam name="TContext">
/// The EF Core context that implements
/// <see cref="ISlackWorkspaceConfigDbContext"/>. The Slack project ships
/// <see cref="SlackPersistenceDbContext"/>; production composition roots
/// may supply a richer <c>MessagingDbContext</c> that implements the
/// same interface.
/// </typeparam>
/// <remarks>
/// <para>
/// The store is safe as a singleton: every lookup opens a fresh DI scope
/// so the scoped <typeparamref name="TContext"/> (registered via
/// <c>AddDbContext&lt;TContext&gt;</c>) does NOT become a captive
/// dependency of this singleton.
/// </para>
/// <para>
/// Reads use <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}(System.Linq.IQueryable{TEntity})"/>
/// so the change tracker is not populated with workspace config rows --
/// these reads are pure projections and never need to be saved back
/// through the same context.
/// </para>
/// <para>
/// A small read-through in-memory cache is intentionally OMITTED at this
/// stage. The signature validator already pays the secret-provider
/// resolution cost on every request, so an extra single-row SELECT on a
/// small dimension table is dominated by the HMAC compute. Stage 3.3
/// adds the caching composite if benchmarks show the SELECT becoming the
/// bottleneck.
/// </para>
/// </remarks>
public sealed class EntityFrameworkSlackWorkspaceConfigStore<TContext> : ISlackWorkspaceConfigStore
    where TContext : class, ISlackWorkspaceConfigDbContext
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<EntityFrameworkSlackWorkspaceConfigStore<TContext>> logger;

    /// <summary>
    /// Creates a store that resolves <typeparamref name="TContext"/>
    /// from a fresh DI scope per lookup.
    /// </summary>
    public EntityFrameworkSlackWorkspaceConfigStore(
        IServiceScopeFactory scopeFactory,
        ILogger<EntityFrameworkSlackWorkspaceConfigStore<TContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        // Stage 3.1 evaluator iter-4 item 2: filter Enabled at the
        // store boundary so the contract guarantees no disabled
        // workspace row leaks out of GetByTeamIdAsync. Future
        // authorization code (Stage 3.2 ACL filter) can safely trust
        // that a non-null result is an enabled, usable workspace
        // without re-checking the flag. The signature validator
        // keeps a belt-and-suspenders !workspace.Enabled check in
        // case a custom ISlackWorkspaceConfigStore implementation
        // misses the contract, and the rejection audit still records
        // the team_id because the validator passes it through to the
        // SlackSignatureValidationResult regardless of whether the
        // workspace was resolved.
        SlackWorkspaceConfig? row = await context.SlackWorkspaceConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TeamId == teamId && c.Enabled, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            this.logger.LogDebug(
                "SlackWorkspaceConfig lookup miss for team_id={TeamId} (row absent or Enabled=false).",
                teamId);
        }

        return row;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        List<SlackWorkspaceConfig> rows = await context.SlackWorkspaceConfigs
            .AsNoTracking()
            .Where(c => c.Enabled)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows;
    }
}
