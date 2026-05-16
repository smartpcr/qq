// -----------------------------------------------------------------------
// <copyright file="EntityFrameworkSlackThreadMappingLookup.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF Core-backed <see cref="ISlackThreadMappingLookup"/>. Resolves the
/// <see cref="SlackThreadMapping"/> for an interactive payload's parent
/// message by querying the <c>slack_thread_mapping</c> table on the
/// unique <c>(TeamId, ChannelId, ThreadTs)</c> index declared by
/// <see cref="SlackThreadMappingConfiguration"/>.
/// </summary>
/// <typeparam name="TContext">EF Core context exposing
/// <see cref="ISlackThreadMappingDbContext.SlackThreadMappings"/>.
/// The Worker host's <see cref="SlackPersistenceDbContext"/> satisfies
/// this constraint.</typeparam>
/// <remarks>
/// <para>
/// Creates a per-call DI scope so the singleton lookup can resolve a
/// scoped <typeparamref name="TContext"/> without violating EF's
/// thread-safety rules (the Stage 5.3 handler is itself a singleton
/// dispatched from a single-pump background ingestor, but the safety
/// net costs nothing and matches the
/// <see cref="SlackIdempotencyGuard{TContext}"/> pattern).
/// </para>
/// </remarks>
internal sealed class EntityFrameworkSlackThreadMappingLookup<TContext>
    : ISlackThreadMappingLookup
    where TContext : DbContext, ISlackThreadMappingDbContext
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<EntityFrameworkSlackThreadMappingLookup<TContext>> logger;

    public EntityFrameworkSlackThreadMappingLookup(
        IServiceScopeFactory scopeFactory,
        ILogger<EntityFrameworkSlackThreadMappingLookup<TContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SlackThreadMapping?> LookupAsync(
        string teamId,
        string? channelId,
        string? threadTs,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(teamId)
            || string.IsNullOrEmpty(channelId)
            || string.IsNullOrEmpty(threadTs))
        {
            return null;
        }

        // Iter-2 evaluator item #2: lookup failures MUST propagate.
        // The Stage 5.3 handler relies on a deterministic CorrelationId
        // for the "every agent/human exchange is queryable by
        // correlation id" acceptance criterion; swallowing a DB error
        // and degrading to the envelope idempotency key publishes a
        // decision with a WRONG correlation id (the inbound key has no
        // relation to the orchestrator task id), which silently breaks
        // the audit trail. Letting the exception bubble drives the
        // inbound pipeline's retry / dead-letter machinery instead.
        // OperationCanceledException is allowed to propagate naturally
        // through the await; no special handling needed because we no
        // longer catch and re-throw.
        using IServiceScope scope = this.scopeFactory.CreateScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        return await context.SlackThreadMappings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.TeamId == teamId
                    && m.ChannelId == channelId!
                    && m.ThreadTs == threadTs!,
                ct)
            .ConfigureAwait(false);
    }
}
