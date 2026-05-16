// -----------------------------------------------------------------------
// <copyright file="SlackOutboundDispatchServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Composition-root wiring for Stage 6.3 -- registers the
/// <see cref="SlackOutboundDispatcher"/> background service, the
/// in-process <see cref="ISlackOutboundQueue"/> +
/// <see cref="ISlackDeadLetterQueue"/> defaults, the shared
/// <see cref="ISlackRateLimiter"/> singleton, the HTTP-backed
/// <see cref="ISlackOutboundDispatchClient"/>, and the
/// <see cref="SlackConnector"/> implementation of
/// <see cref="IMessengerConnector"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implementation-plan.md Stage 6.3 steps 1-8 -- this extension is the
/// canonical wiring path; hosts that prefer the durable upstream
/// queue / DLQ implementations can register them BEFORE calling this
/// extension and the <c>TryAdd*</c> registrations here will defer to
/// them.
/// </para>
/// <para>
/// Mirrors the wiring shape used by
/// <see cref="SlackThreadLifecycleServiceCollectionExtensions"/>
/// (Stage 6.2): the named <see cref="System.Net.Http.IHttpClientFactory"/>
/// entry is always added so resilience-handler layering remains
/// possible; the service-type registrations use <c>TryAdd*</c> so
/// host-supplied overrides registered earlier still win.
/// </para>
/// </remarks>
public static class SlackOutboundDispatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stage 6.3 outbound dispatcher and its
    /// collaborators against <paramref name="services"/>. Binds
    /// <see cref="SlackOutboundOptions"/> and
    /// <see cref="SlackOutboundDispatchClientOptions"/> from the
    /// supplied <see cref="IConfiguration"/> when present.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">
    /// Root configuration; the <c>"Slack:Outbound"</c> section
    /// supplies both the connector <see cref="SlackOutboundOptions"/>
    /// (<c>DefaultTeamId</c>) and the dispatch-client
    /// <see cref="SlackOutboundDispatchClientOptions"/>
    /// (<c>RequestTimeout</c>). The property names do not collide so
    /// both bind cleanly from the same section.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddSlackOutboundDispatcher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Named HttpClient so production deployments can layer their
        // own retry / circuit-breaker / telemetry handlers without
        // subclassing the dispatch client.
        services.AddHttpClient(HttpClientSlackOutboundDispatchClient.HttpClientName);

        // Options binding -- both options classes share the
        // "Slack:Outbound" section. Property names do not overlap.
        services
            .AddOptions<SlackOutboundOptions>()
            .Bind(configuration.GetSection(SlackOutboundOptions.SectionName));

        services
            .AddOptions<SlackOutboundDispatchClientOptions>()
            .Bind(configuration.GetSection(SlackOutboundDispatchClientOptions.SectionName));

        // In-process queue + DLQ defaults. Production hosts that wire
        // the durable upstream implementations from
        // AgentSwarm.Messaging.Core ahead of this call keep their
        // registrations (TryAdd).
        services.TryAddSingleton<ISlackOutboundQueue, ChannelBasedSlackOutboundQueue>();
        services.TryAddSingleton<ISlackDeadLetterQueue, InMemorySlackDeadLetterQueue>();

        // Default retry policy (Stage 4.3). The dispatcher reuses the
        // same backoff curve for transient Slack failures.
        services.TryAddSingleton<ISlackRetryPolicy, DefaultSlackRetryPolicy>();

        // Shared per-tier token-bucket limiter -- MUST be a singleton
        // so the outbound dispatcher and Stage 6.4's
        // SlackDirectApiClient (when it lands) collectively respect
        // Slack's published tier ceilings.
        services.TryAddSingleton<ISlackRateLimiter, SlackTokenBucketRateLimiter>();

        // HTTP-backed dispatch client. TryAdd so a host-supplied stub
        // (e.g. for integration tests) wins.
        services.TryAddSingleton<ISlackOutboundDispatchClient, HttpClientSlackOutboundDispatchClient>();

        // Slack implementation of the platform-neutral connector.
        services.TryAddSingleton<Rendering.ISlackMessageRenderer, Rendering.DefaultSlackMessageRenderer>();
        services.TryAddSingleton<SlackConnector>();
        services.TryAddSingleton<IMessengerConnector>(sp =>
            sp.GetRequiredService<SlackConnector>());

        // Drain loop -- registered as an IHostedService so the .NET
        // generic host starts/stops it alongside the inbound ingestor.
        services.AddHostedService<SlackOutboundDispatcher>();

        return services;
    }
}
