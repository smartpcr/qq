// -----------------------------------------------------------------------
// <copyright file="MessagingPersistenceServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Composition-root entry point for the cross-platform persistence
/// primitives shipped by <c>AgentSwarm.Messaging.Persistence</c>. The
/// Stage 8.1 implementation plan calls for the Worker host to wire
/// the persistence project's registrations through this single
/// extension so every connector composition root inherits the same
/// baseline (shared <c>MessagingDbContext</c>, entity configurations
/// for the audit log / idempotency store / thread mappings, retention
/// policies) without each project owning a copy.
/// </summary>
/// <remarks>
/// <para>
/// Implementation-plan.md Stage 8.1: "Wire the Worker project
/// <c>Program.cs</c> to call <c>AddSlackMessenger()</c> and
/// <c>AddSecretProvider()</c> for Slack-owned registrations, plus
/// the upstream <c>AddMessagingCore()</c> and
/// <c>AddMessagingPersistence()</c> DI registrations (provided by
/// the Core and Persistence projects when available)".
/// </para>
/// <para>
/// The full cross-platform <c>MessagingDbContext</c> and entity
/// configurations are still being built out by the upstream Persistence
/// story. The Slack-specific
/// <see cref="AgentSwarm.Messaging.Slack.Persistence.SlackPersistenceDbContext"/>
/// remains owned by the Slack connector and is wired by
/// <c>AddSlackMessenger</c>. This Stage 8.1 entry point exists so the
/// Worker host's <c>Program.cs</c> already calls
/// <c>AddMessagingPersistence()</c>; the cross-platform story can
/// land its concrete registrations later without changing the host
/// composition root.
/// </para>
/// </remarks>
public static class MessagingPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cross-platform persistence primitives owned by
    /// <c>AgentSwarm.Messaging.Persistence</c>. Safe to call multiple
    /// times -- the extension is currently a no-op shim that reserves
    /// the API surface; future Persistence-story releases populate
    /// concrete registrations (e.g. a shared <c>MessagingDbContext</c>
    /// with the audit / idempotency / thread-mapping entity
    /// configurations) without requiring callers to change their
    /// composition root.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">Configuration root. Reserved for
    /// the upstream Persistence story's options binding (connection
    /// strings, migration toggles, retention sweep cadence).</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddMessagingPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Intentionally narrow: the upstream Persistence story owns
        // the concrete cross-platform registrations. Today the
        // connector-specific SlackPersistenceDbContext is wired by
        // AddSlackMessenger (Stage 8.1) and not by this extension.
        return services;
    }
}
