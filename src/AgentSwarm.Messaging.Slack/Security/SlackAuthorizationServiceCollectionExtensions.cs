// -----------------------------------------------------------------------
// <copyright file="SlackAuthorizationServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extensions that register the Stage 3.2
/// <see cref="SlackAuthorizationFilter"/> together with its supporting
/// services (<see cref="ISlackMembershipResolver"/>,
/// <see cref="ISlackUserGroupClient"/>,
/// <see cref="ISlackAuthorizationAuditSink"/>, and
/// <see cref="SlackAuthorizationOptions"/>).
/// </summary>
/// <remarks>
/// Mirrors <see cref="SlackSignatureValidationServiceCollectionExtensions"/>:
/// every dependency is registered via
/// <see cref="ServiceCollectionDescriptorExtensions.TryAdd"/> so callers can
/// pre-register production overrides (a database-backed audit writer, a
/// fake user-group client in tests, etc.) before invoking this extension.
/// The extension is idempotent -- repeated calls do not duplicate option
/// validators or replace previously registered implementations.
/// </remarks>
public static class SlackAuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="SlackAuthorizationFilter"/> and its
    /// dependencies. Caller is responsible for invoking
    /// <c>AddControllers().AddMvcOptions(opts =&gt; opts.Filters.AddService&lt;SlackAuthorizationFilter&gt;())</c>
    /// to actually mount the filter on the MVC pipeline -- this
    /// extension only takes care of DI registrations so that test hosts
    /// without controllers can still resolve the filter for unit
    /// tests.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">Configuration root bound to
    /// <see cref="SlackAuthorizationOptions.SectionName"/>.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddSlackAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SlackAuthorizationOptions>()
            .Bind(configuration.GetSection(SlackAuthorizationOptions.SectionName))
            .Validate(
                opts => opts.PathPrefix is null || !opts.PathPrefix.Contains(' '),
                $"{nameof(SlackAuthorizationOptions)}.{nameof(SlackAuthorizationOptions.PathPrefix)} must not contain whitespace; set it to a URL prefix like '/api/slack' or leave empty to disable the path guard.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // The Stage 3.1 extension already registers in-memory defaults
        // for the workspace store, secret provider, and audit writer.
        // We TryAdd the same fall-backs here so calling
        // AddSlackAuthorization in isolation (e.g. a test host that
        // skips signature validation) still resolves cleanly.
        services.TryAddSingleton<ISecretProvider, InMemorySecretProvider>();
        services.TryAddSingleton<ISlackWorkspaceConfigStore, InMemorySlackWorkspaceConfigStore>();
        services.TryAddSingleton<InMemorySlackAuditEntryWriter>();
        services.TryAddSingleton<ISlackAuditEntryWriter>(sp =>
            sp.GetRequiredService<InMemorySlackAuditEntryWriter>());

        // SlackAuditEntryAuthorizationSink is the canonical sink: it
        // bridges SlackAuthorizationAuditRecord -> SlackAuditEntry ->
        // ISlackAuditEntryWriter. Callers that need the raw record
        // stream (e.g., a structured-logging fan-out) register their
        // own ISlackAuthorizationAuditSink BEFORE this call.
        services.TryAddSingleton<ISlackAuthorizationAuditSink, SlackAuditEntryAuthorizationSink>();

        services.TryAddSingleton<ISlackUserGroupClient, SlackNetUserGroupClient>();
        services.TryAddSingleton<ISlackMembershipResolver, SlackMembershipResolver>();

        // The filter itself is registered as a transient + singleton:
        // the DI container resolves it through
        // MvcOptions.Filters.AddService when the host wires controllers
        // (Stage 4.1). For now we register it as a singleton so it can
        // be resolved directly by integration tests.
        services.AddSingleton<SlackAuthorizationFilter>();

        return services;
    }
}
