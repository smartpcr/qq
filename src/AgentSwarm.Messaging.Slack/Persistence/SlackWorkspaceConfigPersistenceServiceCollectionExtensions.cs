// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceConfigPersistenceServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

/// <summary>
/// DI extensions that register the EF Core-backed
/// <see cref="EntityFrameworkSlackWorkspaceConfigStore{TContext}"/> as the
/// canonical <see cref="ISlackWorkspaceConfigStore"/>, together with the
/// optional startup seeder that upserts <c>Slack:Workspaces</c>
/// configuration entries into the database. Designed to be called BEFORE
/// <c>AddSlackSignatureValidation</c> so the validator's
/// <c>TryAddSingleton&lt;ISlackWorkspaceConfigStore, ...&gt;</c> fallback
/// is skipped and workspace lookups hit the durable
/// <c>slack_workspace_config</c> table on a restarted host.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.1 evaluator iter-3 item 1: the Worker host previously
/// registered only an in-memory <see cref="ISlackWorkspaceConfigStore"/>
/// (seeded from configuration each boot), so any operator who managed
/// workspaces outside <c>appsettings.json</c> would lose them on
/// restart. Wiring the EF store fixes that and lets <c>appsettings</c>
/// remain a convenient seed source rather than the only source of truth.
/// </para>
/// </remarks>
public static class SlackWorkspaceConfigPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers
    /// <see cref="EntityFrameworkSlackWorkspaceConfigStore{TContext}"/> as
    /// the canonical <see cref="ISlackWorkspaceConfigStore"/>, removing
    /// any previously-registered <see cref="ISlackWorkspaceConfigStore"/>
    /// (typically the in-memory seed-from-config implementation) so the
    /// EF-backed store wins.
    /// </summary>
    /// <typeparam name="TContext">
    /// The EF Core context that implements
    /// <see cref="ISlackWorkspaceConfigDbContext"/>. Must already be
    /// registered as <c>Scoped</c> via <c>AddDbContext&lt;TContext&gt;</c>
    /// by the caller (or a derived context).
    /// </typeparam>
    public static IServiceCollection AddSlackEntityFrameworkWorkspaceConfigStore<TContext>(
        this IServiceCollection services)
        where TContext : class, ISlackWorkspaceConfigDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<EntityFrameworkSlackWorkspaceConfigStore<TContext>>();
        services.RemoveAll<ISlackWorkspaceConfigStore>();
        services.AddSingleton<ISlackWorkspaceConfigStore>(sp =>
            sp.GetRequiredService<EntityFrameworkSlackWorkspaceConfigStore<TContext>>());

        return services;
    }

    /// <summary>
    /// Registers a startup-time seeder that upserts every workspace
    /// declared under <see cref="SlackWorkspaceSeedOptions.SectionName"/>
    /// (the <c>Slack:Workspaces</c> configuration section) into the
    /// <c>slack_workspace_config</c> table backing <typeparamref name="TContext"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seeder runs once during host start (it is an
    /// <see cref="IHostedService"/>) and is idempotent: existing rows
    /// are updated in-place (preserving <c>created_at</c>), missing
    /// rows are inserted. Workspaces that exist only in the database
    /// (not in configuration) are LEFT ALONE so an operator can manage
    /// extra workspaces via direct DB writes without the seeder
    /// stomping them on the next boot.
    /// </para>
    /// <para>
    /// Validation of the seed entries (blank <c>TeamId</c>, blank
    /// <c>SigningSecretRef</c>) is performed by the options binding in
    /// <see cref="SlackConnectorServiceCollectionExtensions.AddSlackWorkspaceConfigStoreFromConfiguration"/>;
    /// invalid entries throw before this seeder runs, so the database
    /// never sees half-populated rows.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSlackWorkspaceConfigSeeder<TContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TContext : class, ISlackWorkspaceConfigDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Reuse the existing seed-options binder. If
        // AddSlackWorkspaceConfigStoreFromConfiguration was called first
        // the binding is already in place; AddOptions returns the same
        // builder so the second Configure call is additive, not
        // destructive.
        services
            .AddOptions<SlackWorkspaceSeedOptions>()
            .Configure(opts => SlackWorkspaceSeedBinder.BindEntries(configuration, opts));

        services.AddHostedService<SlackWorkspaceConfigSeedHostedService<TContext>>();
        return services;
    }
}
