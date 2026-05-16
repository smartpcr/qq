// -----------------------------------------------------------------------
// <copyright file="SlackInboundDurabilityServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extensions that pull forward the durable
/// <c>SlackInboundRequestRecord</c>-backed idempotency store for the
/// Stage 4.1 modal fast-path. Registers a two-level
/// <see cref="ISlackFastPathIdempotencyStore"/> -- in-process L1 + EF
/// L2 -- keyed on a caller-supplied <typeparamref name="TContext"/>
/// that already implements
/// <see cref="ISlackInboundRequestRecordDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.1 evaluator iter-2 item 2 fix. The Worker host calls this
/// extension after registering its
/// <see cref="SlackPersistenceDbContext"/>; test hosts skip the call
/// and the
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddSlackInboundTransport"/>
/// default (in-process-only L1) is used.
/// </para>
/// <para>
/// All bindings use <c>services.RemoveAll&lt;T&gt;() + AddSingleton</c>
/// so calling this extension after
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddSlackInboundTransport"/>
/// reliably swaps in the durable composite store even when the order
/// is reversed.
/// </para>
/// </remarks>
public static class SlackInboundDurabilityServiceCollectionExtensions
{
    /// <summary>
    /// Wires the durable idempotency store against
    /// <typeparamref name="TContext"/>. The composite first probes the
    /// in-process L1 (catches sub-second Slack retries that hit the
    /// same pod) and then the EF L2 (catches retries that cross a pod
    /// restart or land on a different replica).
    /// </summary>
    /// <typeparam name="TContext">
    /// EF Core context that implements
    /// <see cref="ISlackInboundRequestRecordDbContext"/> and is
    /// registered via <c>AddDbContext&lt;TContext&gt;</c>.
    /// </typeparam>
    public static IServiceCollection AddSlackFastPathDurableIdempotency<TContext>(
        this IServiceCollection services)
        where TContext : DbContext, ISlackInboundRequestRecordDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        // L2 store: durable EF-backed.
        services.TryAddSingleton<EntityFrameworkSlackFastPathIdempotencyStore<TContext>>();

        // L1 cache: in-process.
        services.TryAddSingleton<SlackInProcessIdempotencyStore>();

        // Composite that wraps L1 + L2 and exposes the
        // ISlackFastPathIdempotencyStore contract the modal fast-path
        // handler depends on.
        services.RemoveAll<ISlackFastPathIdempotencyStore>();
        services.AddSingleton<ISlackFastPathIdempotencyStore>(sp =>
            new CompositeSlackFastPathIdempotencyStore(
                sp.GetRequiredService<SlackInProcessIdempotencyStore>(),
                sp.GetRequiredService<EntityFrameworkSlackFastPathIdempotencyStore<TContext>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CompositeSlackFastPathIdempotencyStore>>()));

        return services;
    }
}

