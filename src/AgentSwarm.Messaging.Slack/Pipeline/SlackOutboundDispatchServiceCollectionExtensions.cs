// -----------------------------------------------------------------------
// <copyright file="SlackOutboundDispatchServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        // DLQ default is the in-memory implementation so dev / test
        // hosts have a zero-config path. A production host that did
        // NOT call AddFileSystemSlackDeadLetterQueue (or wire a
        // durable upstream implementation) BEFORE this extension
        // would silently end up with a volatile DLQ -- a message that
        // exhausts retries, gets dead-lettered, and then hits a
        // process restart would be lost, violating FR-005 (durable
        // dead-letter queue) and FR-007 (zero tolerated message
        // loss). The InMemorySlackDeadLetterQueueStartupWarning
        // hosted service below resolves the registered
        // ISlackDeadLetterQueue at startup and emits a LogWarning if
        // the concrete type is the in-memory implementation so
        // operators are alerted to the non-durable path. The warning
        // does NOT fire when a host has overridden the binding (via
        // AddFileSystemSlackDeadLetterQueue or any other durable
        // implementation).
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

        // Startup warning for the non-durable in-memory DLQ default.
        // AddHostedService<T> registers via TryAddEnumerable, so a
        // host that calls AddSlackOutboundDispatcher twice still ends
        // up with a single warning instance.
        services.AddHostedService<InMemorySlackDeadLetterQueueStartupWarning>();

        return services;
    }
}

/// <summary>
/// <see cref="IHostedService"/> that runs once at startup and emits a
/// <see cref="LogLevel.Warning"/> when the registered
/// <see cref="ISlackDeadLetterQueue"/> is the non-durable
/// <see cref="InMemorySlackDeadLetterQueue"/> default.
/// </summary>
/// <remarks>
/// <para>
/// Addresses the Stage 6.3 evaluator concern that a production host
/// could ship with the in-memory DLQ default and silently lose
/// dead-lettered envelopes across a process restart, violating
/// FR-005 (durable dead-letter queue) and FR-007 (zero tolerated
/// message loss). The warning is logged once at
/// <see cref="StartAsync"/> with explicit guidance on how to wire a
/// durable replacement (<c>AddFileSystemSlackDeadLetterQueue</c> or
/// any upstream <c>AgentSwarm.Messaging.Core</c> implementation
/// registered against <see cref="ISlackDeadLetterQueue"/> BEFORE
/// <see cref="SlackOutboundDispatchServiceCollectionExtensions.AddSlackOutboundDispatcher"/>).
/// </para>
/// <para>
/// Type-equality check (<c>GetType() == typeof(...)</c>) -- a host
/// that registered a different <see cref="ISlackDeadLetterQueue"/>
/// implementation (durable upstream, custom test double, etc.) does
/// NOT trigger the warning. The check intentionally uses an exact
/// type comparison so a future durable subclass would not
/// accidentally suppress the warning if the in-memory default were
/// ever re-introduced as a base class.
/// </para>
/// </remarks>
internal sealed class InMemorySlackDeadLetterQueueStartupWarning : IHostedService
{
    private readonly ISlackDeadLetterQueue deadLetterQueue;
    private readonly ILogger<InMemorySlackDeadLetterQueueStartupWarning> logger;

    public InMemorySlackDeadLetterQueueStartupWarning(
        ISlackDeadLetterQueue deadLetterQueue,
        ILogger<InMemorySlackDeadLetterQueueStartupWarning> logger)
    {
        this.deadLetterQueue = deadLetterQueue ?? throw new ArgumentNullException(nameof(deadLetterQueue));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (this.deadLetterQueue.GetType() == typeof(InMemorySlackDeadLetterQueue))
        {
            this.logger.LogWarning(
                "Slack outbound dead-letter queue is registered as the in-memory implementation ({DeadLetterQueueType}). Dead-lettered envelopes will NOT survive a process restart, violating FR-005 (durable dead-letter queue) and FR-007 (zero tolerated message loss). For production deployments, register a durable implementation BEFORE calling AddSlackOutboundDispatcher -- e.g. services.AddFileSystemSlackDeadLetterQueue(directoryPath) -- so the TryAddSingleton fallback in AddSlackOutboundDispatcher becomes a no-op.",
                nameof(InMemorySlackDeadLetterQueue));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
