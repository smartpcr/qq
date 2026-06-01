// -----------------------------------------------------------------------
// <copyright file="MessagingCoreServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Composition-root entry point for the cross-platform messaging
/// primitives shipped by <c>AgentSwarm.Messaging.Core</c>. The
/// Stage 8.1 implementation plan calls for the Worker host to wire
/// connector-agnostic registrations through this single extension so
/// every connector composition root (Slack today; Telegram, Discord,
/// Teams tomorrow) inherits the same baseline -- shared
/// <see cref="TimeProvider"/>, secret-resolution chain, options
/// bindings -- without each project owning a copy of the boilerplate.
/// </summary>
/// <remarks>
/// <para>
/// Implementation-plan.md Stage 8.1: "Wire the Worker project
/// <c>Program.cs</c> to call <c>AddSlackMessenger()</c> and
/// <c>AddSecretProvider()</c> for Slack-owned registrations, plus
/// the upstream <c>AddMessagingCore()</c> and
/// <c>AddMessagingPersistence()</c> DI registrations (provided by the
/// Core and Persistence projects when available)".
/// </para>
/// <para>
/// The full Core wiring (durable queues, retry primitives, audit
/// pipeline, deduplication store) is still being built out by the
/// upstream cross-platform story; this Stage 8.1 entry point keeps
/// the API surface stable so the Worker host does not need to be
/// re-shaped each time a new Core registration lands. Today the
/// extension is intentionally narrow:
/// <list type="bullet">
/// <item>Registers <see cref="TimeProvider.System"/> as the default
/// <see cref="TimeProvider"/> using <c>TryAddSingleton</c> so a
/// faked time source registered by a test composition root still
/// wins.</item>
/// <item>Reserves the
/// <see cref="MessagingCoreOptions.SectionName"/> configuration
/// section so future cross-platform knobs can be bound without
/// requiring callers to change their composition root.</item>
/// </list>
/// </para>
/// </remarks>
public static class MessagingCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cross-platform messaging primitives owned by
    /// <c>AgentSwarm.Messaging.Core</c>. Safe to call multiple times --
    /// all bindings use <c>TryAdd*</c> so a pre-registered override
    /// (e.g., a fake <see cref="TimeProvider"/> in tests) wins.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">Configuration root. Reserved for
    /// future cross-platform options; today the extension binds only
    /// the <see cref="MessagingCoreOptions.SectionName"/> placeholder
    /// so a host that ships an empty section in
    /// <c>appsettings.json</c> still resolves a valid
    /// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>
    /// instance.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddMessagingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // TimeProvider.System is the canonical clock the Slack
        // connector (and every future cross-platform primitive)
        // depends on. The TryAdd registration lets a test composition
        // root override it with a FakeTimeProvider BEFORE this call.
        services.TryAddSingleton(TimeProvider.System);

        // Reserve the configuration section so a host that lists
        // "Messaging:Core" in appsettings.json gets a bound options
        // instance even before the cross-platform story populates
        // concrete knobs. The placeholder keeps the API surface
        // stable across Core releases.
        services
            .AddOptions<MessagingCoreOptions>()
            .Bind(configuration.GetSection(MessagingCoreOptions.SectionName));

        return services;
    }
}

/// <summary>
/// Placeholder options class for the
/// <see cref="MessagingCoreServiceCollectionExtensions.AddMessagingCore"/>
/// extension. Bound from the <see cref="SectionName"/> configuration
/// section. Reserved for future cross-platform knobs supplied by the
/// upstream Core story; intentionally empty today so the API surface
/// stays stable across Core releases.
/// </summary>
public sealed class MessagingCoreOptions
{
    /// <summary>
    /// Configuration section name bound by
    /// <see cref="MessagingCoreServiceCollectionExtensions.AddMessagingCore"/>:
    /// <c>"Messaging:Core"</c>.
    /// </summary>
    public const string SectionName = "Messaging:Core";
}
