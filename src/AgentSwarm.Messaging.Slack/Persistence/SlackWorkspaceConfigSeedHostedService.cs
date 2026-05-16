// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceConfigSeedHostedService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Startup-time <see cref="IHostedService"/> that upserts every
/// <see cref="SlackWorkspaceSeedEntry"/> declared in
/// <see cref="SlackWorkspaceSeedOptions.SectionName"/> into the
/// <c>slack_workspace_config</c> table backing
/// <typeparamref name="TContext"/>. Closes Stage 3.1 evaluator iter-3
/// item 1 by guaranteeing that a freshly-deployed Worker has at least
/// the configured workspaces available to
/// <see cref="EntityFrameworkSlackWorkspaceConfigStore{TContext}"/>
/// without the operator having to seed the database out of band.
/// </summary>
/// <typeparam name="TContext">
/// EF Core context implementing <see cref="ISlackWorkspaceConfigDbContext"/>.
/// </typeparam>
/// <remarks>
/// <para>
/// The seeder is idempotent: an existing row keyed by
/// <see cref="SlackWorkspaceConfig.TeamId"/> is updated in place
/// (preserving its original <see cref="SlackWorkspaceConfig.CreatedAt"/>)
/// and missing rows are inserted. Rows that exist only in the database
/// (not in configuration) are LEFT ALONE so an operator can add extra
/// workspaces via direct DB writes without the seeder stomping them on
/// the next host boot.
/// </para>
/// <para>
/// The seeder runs synchronously on <see cref="StartAsync"/>. If the
/// upsert fails the host start aborts -- a half-seeded
/// <c>slack_workspace_config</c> table is preferable to silently
/// continuing with stale data.
/// </para>
/// </remarks>
public sealed class SlackWorkspaceConfigSeedHostedService<TContext> : IHostedService
    where TContext : class, ISlackWorkspaceConfigDbContext
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<SlackWorkspaceSeedOptions> options;
    private readonly ILogger<SlackWorkspaceConfigSeedHostedService<TContext>> logger;

    /// <summary>Creates a seeder bound to the supplied DI scope factory and options.</summary>
    public SlackWorkspaceConfigSeedHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<SlackWorkspaceSeedOptions> options,
        ILogger<SlackWorkspaceConfigSeedHostedService<TContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SlackWorkspaceSeedOptions resolved = this.options.Value ?? new SlackWorkspaceSeedOptions();
        IReadOnlyList<SlackWorkspaceConfig> seeds = MaterializeSeeds(resolved);
        if (seeds.Count == 0)
        {
            this.logger.LogInformation(
                "No {Section} entries configured; the slack_workspace_config table will be left untouched.",
                SlackWorkspaceSeedOptions.SectionName);
            return;
        }

        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        int inserted = 0;
        int updated = 0;
        foreach (SlackWorkspaceConfig seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SlackWorkspaceConfig? existing = await context.SlackWorkspaceConfigs
                .FirstOrDefaultAsync(c => c.TeamId == seed.TeamId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                context.SlackWorkspaceConfigs.Add(seed);
                inserted++;
                continue;
            }

            // Update mutable fields; PRESERVE created_at so audit
            // queries that pivot on row age stay accurate.
            existing.WorkspaceName = seed.WorkspaceName;
            existing.BotTokenSecretRef = seed.BotTokenSecretRef;
            existing.SigningSecretRef = seed.SigningSecretRef;
            existing.AppLevelTokenRef = seed.AppLevelTokenRef;
            existing.DefaultChannelId = seed.DefaultChannelId;
            existing.FallbackChannelId = seed.FallbackChannelId;
            existing.AllowedChannelIds = seed.AllowedChannelIds;
            existing.AllowedUserGroupIds = seed.AllowedUserGroupIds;
            existing.Enabled = seed.Enabled;
            existing.UpdatedAt = seed.UpdatedAt;
            updated++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        this.logger.LogInformation(
            "Seeded slack_workspace_config from {Section}: {Inserted} inserted, {Updated} updated.",
            SlackWorkspaceSeedOptions.SectionName,
            inserted,
            updated);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static IReadOnlyList<SlackWorkspaceConfig> MaterializeSeeds(SlackWorkspaceSeedOptions resolved)
    {
        List<SlackWorkspaceConfig> list = new();
        foreach (SlackWorkspaceConfig seed in SlackWorkspaceSeedBinder.Materialize(resolved))
        {
            list.Add(seed);
        }

        return list;
    }
}
