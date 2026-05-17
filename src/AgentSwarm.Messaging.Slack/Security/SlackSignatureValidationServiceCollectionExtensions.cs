// -----------------------------------------------------------------------
// <copyright file="SlackSignatureValidationServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI and pipeline extensions that register the Stage 3.1
/// <see cref="SlackSignatureValidator"/> middleware together with its
/// supporting services.
/// </summary>
/// <remarks>
/// Splitting this from the broader <c>AddSlackConnectorOptions</c>
/// extension keeps the signature pipeline self-contained: a future test
/// host (Stage 4.1) can opt into just the validator without spinning up
/// the entire connector. The extension is deliberately idempotent --
/// repeated calls do not re-register option validators or replace
/// previously supplied <see cref="ISecretProvider"/> / audit-sink
/// implementations.
/// </remarks>
public static class SlackSignatureValidationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="SlackSignatureValidator"/> middleware
    /// together with default fall-back implementations of every
    /// dependency. The bindings use <see cref="ServiceCollectionDescriptorExtensions.TryAdd"/>
    /// so callers can register their own production
    /// <see cref="ISecretProvider"/>, <see cref="ISlackWorkspaceConfigStore"/>,
    /// and <see cref="ISlackSignatureAuditSink"/> implementations before
    /// or after calling this method.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">Configuration root bound to
    /// <see cref="SlackSignatureOptions.SectionName"/>.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddSlackSignatureValidation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SlackSignatureOptions>()
            .Bind(configuration.GetSection(SlackSignatureOptions.SectionName))
            .Validate(
                opts => opts.ClockSkewMinutes >= 0,
                $"{nameof(SlackSignatureOptions)}.{nameof(SlackSignatureOptions.ClockSkewMinutes)} must be non-negative.")
            .Validate(
                opts => opts.MaxBufferedBodyBytes > 0,
                $"{nameof(SlackSignatureOptions)}.{nameof(SlackSignatureOptions.MaxBufferedBodyBytes)} must be positive.")
            .Validate(
                opts => !string.IsNullOrWhiteSpace(opts.PathPrefix)
                    && opts.PathPrefix.StartsWith('/')
                    && !opts.PathPrefix.Contains(' '),
                $"{nameof(SlackSignatureOptions)}.{nameof(SlackSignatureOptions.PathPrefix)} must be a non-empty URL prefix starting with '/' (e.g. '/api/slack'). It cannot contain whitespace and must be a valid PathString; SlackAuthorizationFilter shares this option for its own path scope, so an invalid value would also bypass the authorization gate.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISecretProvider, InMemorySecretProvider>();
        services.TryAddSingleton<ISlackWorkspaceConfigStore, InMemorySlackWorkspaceConfigStore>();

        // Stage 3.3: surface every workspace's secret references to the
        // SecretCacheWarmupHostedService so the composite provider's
        // cache is populated at host start-up rather than on the first
        // request (architecture.md §7.3). TryAddEnumerable is used so
        // multiple ref sources from different connectors can coexist
        // without overwriting each other.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ISecretRefSource, SlackWorkspaceSecretRefSource>());

        // Audit pipeline: persist EVERY rejection through the canonical
        // slack_audit_entry table (Stage 2.1 / 2.2). Production hosts
        // register an EntityFrameworkSlackAuditEntryWriter<TContext>
        // BEFORE this call -- TryAdd lets that override win. Tests and
        // developer setups fall back to the in-memory writer.
        services.TryAddSingleton<InMemorySlackAuditEntryWriter>();
        services.TryAddSingleton<ISlackAuditEntryWriter>(sp =>
            sp.GetRequiredService<InMemorySlackAuditEntryWriter>());

        // SlackAuditEntrySignatureSink is the canonical sink: it bridges
        // SlackSignatureAuditRecord -> SlackAuditEntry -> ISlackAuditEntryWriter.
        // Callers that need the raw record stream (e.g., a structured-
        // logging fan-out) register their own ISlackSignatureAuditSink
        // BEFORE this call.
        services.TryAddSingleton<ISlackSignatureAuditSink, SlackAuditEntrySignatureSink>();

        services.AddSingleton<SlackSignatureValidator>();

        return services;
    }

    /// <summary>
    /// Mounts the registered <see cref="SlackSignatureValidator"/> on the
    /// <paramref name="app"/> request pipeline.
    /// </summary>
    /// <remarks>
    /// Both this extension and the middleware itself respect
    /// <see cref="SlackSignatureOptions.PathPrefix"/>; mounting at the
    /// pipeline root is therefore safe even when the host also serves
    /// non-Slack routes.
    /// </remarks>
    public static IApplicationBuilder UseSlackSignatureValidation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SlackSignatureValidator>();
    }
}
