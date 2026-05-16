// -----------------------------------------------------------------------
// <copyright file="SlackThreadLifecycleServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Composition-root wiring for Stage 6.2 -- registers
/// <see cref="SlackThreadManager{TContext}"/> as the production
/// <see cref="ISlackThreadManager"/>, the HTTP-backed
/// <see cref="HttpClientSlackChatPostMessageClient"/> as the default
/// <see cref="ISlackChatPostMessageClient"/>, and the matching
/// options binding for
/// <see cref="SlackChatPostMessageClientOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the wiring shape used by Stage 5.3's
/// <see cref="SlackInteractionDispatchServiceCollectionExtensions"/>:
/// HttpClient registrations are unconditional (so the named
/// <see cref="System.Net.Http.IHttpClientFactory"/> entry always
/// exists for resilience-handler layering), but the service-type
/// registrations use <c>TryAdd*</c> so host-supplied overrides
/// registered earlier still win.
/// </para>
/// <para>
/// The thread manager itself is registered with
/// <c>RemoveAll</c> + <c>AddSingleton</c> because the whole purpose
/// of this extension is to replace any earlier no-op or test stand-in.
/// </para>
/// </remarks>
public static class SlackThreadLifecycleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stage 6.2 thread manager and its collaborators
    /// against <paramref name="services"/>.
    /// </summary>
    /// <typeparam name="TContext">EF Core context exposing
    /// <see cref="ISlackThreadMappingDbContext.SlackThreadMappings"/>.
    /// The Worker host's <see cref="SlackPersistenceDbContext"/>
    /// satisfies the constraint. Must already be registered as
    /// <c>Scoped</c> via <c>AddDbContext&lt;TContext&gt;</c>.</typeparam>
    /// <param name="services">Target service collection.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddSlackThreadLifecycleManagement<TContext>(
        this IServiceCollection services)
        where TContext : DbContext, ISlackThreadMappingDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        // Named HttpClient so production deployments can layer their
        // own retry / circuit-breaker / telemetry handlers without
        // subclassing the post-message client.
        services.AddHttpClient(HttpClientSlackChatPostMessageClient.HttpClientName);

        // Options binding picks up Slack:ChatPostMessage from the
        // configuration when present. The defaults (10-second
        // request timeout) are baked into SlackChatPostMessageClientOptions
        // so an absent section is fine.
        services.AddOptions<SlackChatPostMessageClientOptions>();

        // Default chat.postMessage client -- TryAdd so a host-supplied
        // production client (notably Stage 6.4's SlackDirectApiClient
        // when it lands) wins. Resolves the per-workspace bot OAuth
        // token via ISlackWorkspaceConfigStore + ISecretProvider, both
        // of which are wired by earlier composition-root extensions
        // (AddSlackEntityFrameworkWorkspaceConfigStore +
        // AddSecretProvider).
        services.TryAddSingleton<ISlackChatPostMessageClient, HttpClientSlackChatPostMessageClient>();

        // Thread manager -- REPLACES any earlier ISlackThreadManager
        // registration. The Stage 6.3 outbound dispatcher consumes
        // this interface; replacing rather than TryAdd-ing means a
        // composition root that wires this extension late (after a
        // test stand-in was registered) still gets the real manager.
        services.RemoveAll<ISlackThreadManager>();
        services.AddSingleton<SlackThreadManager<TContext>>();
        services.AddSingleton<ISlackThreadManager>(sp =>
            sp.GetRequiredService<SlackThreadManager<TContext>>());

        return services;
    }

    /// <summary>
    /// Optional helper: binds
    /// <see cref="SlackChatPostMessageClientOptions"/> from a custom
    /// <see cref="IConfiguration"/> root (the default registration
    /// reads from <c>"Slack:ChatPostMessage"</c> only when the host
    /// has not supplied an explicit binding).
    /// </summary>
    public static IServiceCollection AddSlackChatPostMessageOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SlackChatPostMessageClientOptions>()
            .Bind(configuration.GetSection(SlackChatPostMessageClientOptions.SectionName));

        return services;
    }
}
