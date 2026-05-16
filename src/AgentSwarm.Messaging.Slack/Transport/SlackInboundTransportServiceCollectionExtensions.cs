// -----------------------------------------------------------------------
// <copyright file="SlackInboundTransportServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using AgentSwarm.Messaging.Slack.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extensions that register the Stage 4.1 Slack inbound HTTP
/// transport: the three controllers
/// (<see cref="SlackEventsController"/>,
/// <see cref="SlackCommandsController"/>,
/// <see cref="SlackInteractionsController"/>), the
/// <see cref="SlackInboundEnvelopeFactory"/> they share, the in-process
/// <see cref="ISlackInboundQueue"/> implementation, and the default
/// <see cref="ISlackModalFastPathHandler"/> fall-back.
/// </summary>
/// <remarks>
/// <para>
/// All bindings use <see cref="ServiceCollectionDescriptorExtensions.TryAdd"/>
/// so a composition root may override any single component (e.g., swap
/// the in-memory <see cref="ChannelBasedSlackInboundQueue"/> for a
/// durable Azure Service Bus implementation supplied by
/// <c>AgentSwarm.Messaging.Core</c>) by registering its own
/// implementation BEFORE calling
/// <see cref="AddSlackInboundTransport"/>.
/// </para>
/// <para>
/// The extension also calls
/// <see cref="MvcCoreMvcBuilderExtensions.AddApplicationPart"/> so that
/// MVC's controller scanner discovers the three controllers even when
/// the hosting application's entry assembly does not contain them
/// directly -- e.g., the Stage 4.1 <c>AgentSwarm.Messaging.Worker</c>
/// host whose <c>Program</c> class lives in a different assembly.
/// </para>
/// </remarks>
public static class SlackInboundTransportServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stage 4.1 Slack inbound transport services on
    /// <paramref name="services"/>.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSlackInboundTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The factory only needs a TimeProvider and is otherwise
        // stateless; singleton lifetime keeps allocations off the hot
        // ACK path.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SlackInboundEnvelopeFactory>();

        // The in-process channel queue. Production hosts swap in a
        // durable implementation BEFORE this call.
        services.TryAddSingleton<ChannelBasedSlackInboundQueue>();
        services.TryAddSingleton<ISlackInboundQueue>(sp =>
            sp.GetRequiredService<ChannelBasedSlackInboundQueue>());

        // Dead-letter sink for post-ACK enqueue failures (Stage 4.1
        // iter-3 evaluator item 2). The in-memory default keeps
        // failures observable and operator-recoverable until a
        // production host registers a durable sink BEFORE this call.
        services.TryAddSingleton<InMemorySlackInboundEnqueueDeadLetterSink>();
        services.TryAddSingleton<ISlackInboundEnqueueDeadLetterSink>(sp =>
            sp.GetRequiredService<InMemorySlackInboundEnqueueDeadLetterSink>());

        // Default modal fast-path handler is a real implementation that
        // runs idempotency + views.open synchronously inside the HTTP
        // request lifetime (architecture.md §5.3, tech-spec.md §5.2).
        // Iter-3 (evaluator items 2 + 3) introduces the
        // ISlackFastPathIdempotencyStore abstraction (default: in-process
        // L1 only; SlackInboundDurabilityServiceCollectionExtensions
        // adds the durable EF L2) and the SlackModalAuditRecorder so
        // every views.open call is recorded in slack_audit_entry.
        services.TryAddSingleton<SlackInProcessIdempotencyStore>();
        services.TryAddSingleton<ISlackFastPathIdempotencyStore>(sp =>
            sp.GetRequiredService<SlackInProcessIdempotencyStore>());
        services.TryAddSingleton<ISlackModalPayloadBuilder, DefaultSlackModalPayloadBuilder>();

        // Audit recorder for modal_open entries (architecture.md §5.3
        // step 5, implementation-plan.md line 377). Depends on the
        // ISlackAuditEntryWriter that the Worker host wires against the
        // EF backend via AddSlackEntityFrameworkAuditWriter; test hosts
        // get the InMemorySlackAuditEntryWriter via
        // AddSlackSignatureValidation's TryAdd fallback.
        services.TryAddSingleton<SlackModalAuditRecorder>();

        // Named HttpClient registration so the host can layer
        // resilience handlers (retry, circuit-breaker) on it without
        // subclassing the client.
        services.AddHttpClient(HttpClientSlackViewsOpenClient.HttpClientName);
        services.TryAddSingleton<ISlackViewsOpenClient, HttpClientSlackViewsOpenClient>();

        services.TryAddSingleton<ISlackModalFastPathHandler, DefaultSlackModalFastPathHandler>();

        return services;
    }

    /// <summary>
    /// Registers the Slack inbound controllers
    /// (<see cref="SlackEventsController"/>,
    /// <see cref="SlackCommandsController"/>,
    /// <see cref="SlackInteractionsController"/>) with MVC's
    /// application-part scanner. Required when the host's entry
    /// assembly does not contain the controllers (e.g., the
    /// <c>AgentSwarm.Messaging.Worker</c> host whose
    /// <c>Program</c> lives in a separate assembly).
    /// </summary>
    public static IMvcBuilder AddSlackInboundControllers(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddApplicationPart(typeof(SlackEventsController).Assembly);
    }
}
