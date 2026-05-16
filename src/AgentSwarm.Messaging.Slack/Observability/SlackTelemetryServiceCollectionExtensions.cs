// -----------------------------------------------------------------------
// <copyright file="SlackTelemetryServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Observability;

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI registration helpers for the Slack messenger connector's
/// OpenTelemetry-compatible telemetry primitives. Stage 7.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 1: "Register `ActivitySource` named `AgentSwarm.Messaging.Slack`
/// for distributed tracing in the Slack project's DI registration".
/// </summary>
/// <remarks>
/// <para>
/// Slack components write directly to the
/// <see cref="SlackTelemetry.ActivitySource"/> and
/// <see cref="SlackTelemetry.Meter"/> singletons; this extension only
/// surfaces the same instances through DI so:
/// </para>
/// <list type="bullet">
///   <item><description>The OpenTelemetry .NET SDK (when the Worker
///   opts in) can resolve <see cref="ActivitySource"/> /
///   <see cref="Meter"/> through DI and call
///   <c>AddSource</c> / <c>AddMeter</c> by instance.</description></item>
///   <item><description>Integration tests can take a constructor-injected
///   handle to the same primitives that production code emits to (no
///   shadow singletons).</description></item>
/// </list>
/// <para>
/// The registration is idempotent (<c>TryAddSingleton</c>) so the
/// Worker can call this from multiple composition roots without
/// double-registering. Calling it more than once does not produce a
/// second <see cref="System.Diagnostics.ActivitySource"/> instance --
/// only the first call wins, and every subsequent caller resolves the
/// exact same object.
/// </para>
/// </remarks>
public static class SlackTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Slack connector's <see cref="ActivitySource"/>,
    /// <see cref="Meter"/>, and the typed <see cref="Counter{T}"/> /
    /// <see cref="Histogram{T}"/> instruments described by
    /// architecture.md §6.3 as DI singletons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Although these primitives are also process-static (every Slack
    /// component reaches them through <see cref="SlackTelemetry"/>
    /// directly), surfacing them in DI lets the Worker host wire
    /// downstream OpenTelemetry exporters by injected instance and
    /// lets tests resolve the same listener handles production uses.
    /// The call is idempotent; multiple invocations from competing
    /// composition roots are safe.
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection being configured.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddSlackTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(SlackTelemetry.ActivitySource);
        services.TryAddSingleton(SlackTelemetry.Meter);
        services.TryAddSingleton(SlackTelemetry.InboundCount);
        services.TryAddSingleton(SlackTelemetry.OutboundCount);
        services.TryAddSingleton(SlackTelemetry.OutboundLatencyMs);
        services.TryAddSingleton(SlackTelemetry.IdempotencyDuplicateCount);
        services.TryAddSingleton(SlackTelemetry.AuthRejectedCount);
        services.TryAddSingleton(SlackTelemetry.RateLimitBackoffCount);

        return services;
    }
}
