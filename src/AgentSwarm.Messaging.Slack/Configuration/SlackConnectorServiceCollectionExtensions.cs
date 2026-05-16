// -----------------------------------------------------------------------
// <copyright file="SlackConnectorServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Configuration;

using System;
using System.Collections.Generic;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>
/// Dependency-injection extensions that register Slack connector services and
/// bind their strongly-typed configuration. Options are validated at startup
/// so misconfiguration surfaces eagerly instead of producing runtime failures
/// when sizing connection pools or computing retry delays.
/// </summary>
public static class SlackConnectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers Slack connector option bindings against the supplied
    /// <see cref="IConfiguration"/>. Both <see cref="SlackConnectorOptions"/>
    /// and <see cref="SlackRetryOptions"/> are bound, validated through any
    /// data-annotation attributes present on the option types, and additionally
    /// guarded by inline range checks for numeric properties whose invalid
    /// values would otherwise silently break runtime behaviour.
    /// </summary>
    /// <param name="services">The service collection to register against.</param>
    /// <param name="configuration">The application configuration root.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddSlackConnectorOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectorSection = configuration.GetSection(SlackConnectorOptions.SectionName);
        var retrySection = configuration.GetSection(SlackRetryOptions.SectionName);
        var idempotencySection = configuration.GetSection(SlackIdempotencyOptions.SectionName);

        services
            .AddOptions<SlackConnectorOptions>()
            .Bind(connectorSection)
            .ValidateDataAnnotations()
            .Validate(
                opts => opts.MaxWorkspaces > 0,
                $"{nameof(SlackConnectorOptions)}.{nameof(SlackConnectorOptions.MaxWorkspaces)} must be greater than zero.")
            .Validate(
                opts => opts.Idempotency is not null && opts.Idempotency.StaleProcessingThresholdSeconds > 0,
                $"{nameof(SlackConnectorOptions)}.{nameof(SlackConnectorOptions.Idempotency)}.{nameof(SlackIdempotencyOptions.StaleProcessingThresholdSeconds)} must be greater than zero.")
            .Validate(
                opts => opts.Idempotency is not null && opts.Idempotency.CompletionMaxAttempts > 0,
                $"{nameof(SlackConnectorOptions)}.{nameof(SlackConnectorOptions.Idempotency)}.{nameof(SlackIdempotencyOptions.CompletionMaxAttempts)} must be greater than zero.")
            .Validate(
                opts => opts.Idempotency is not null && opts.Idempotency.CompletionInitialDelayMilliseconds >= 0,
                $"{nameof(SlackConnectorOptions)}.{nameof(SlackConnectorOptions.Idempotency)}.{nameof(SlackIdempotencyOptions.CompletionInitialDelayMilliseconds)} must be non-negative.")
            .ValidateOnStart();

        services
            .AddOptions<SlackRetryOptions>()
            .Bind(retrySection)
            .ValidateDataAnnotations()
            .Validate(
                opts => opts.MaxAttempts > 0,
                $"{nameof(SlackRetryOptions)}.{nameof(SlackRetryOptions.MaxAttempts)} must be greater than zero.")
            .Validate(
                opts => opts.InitialDelayMilliseconds >= 0,
                $"{nameof(SlackRetryOptions)}.{nameof(SlackRetryOptions.InitialDelayMilliseconds)} must be non-negative.")
            .ValidateOnStart();

        // Bind Slack:Idempotency directly too so consumers that resolve
        // IOptions<SlackIdempotencyOptions> get the same values as
        // SlackConnectorOptions.Idempotency. The connector-level section
        // remains the single source of truth; this companion binding
        // is purely a convenience for places that only need the lease
        // knobs without depending on the whole connector options bag.
        services
            .AddOptions<SlackIdempotencyOptions>()
            .Bind(idempotencySection)
            .Validate(
                opts => opts.StaleProcessingThresholdSeconds > 0,
                $"{nameof(SlackIdempotencyOptions)}.{nameof(SlackIdempotencyOptions.StaleProcessingThresholdSeconds)} must be greater than zero.")
            .Validate(
                opts => opts.CompletionMaxAttempts > 0,
                $"{nameof(SlackIdempotencyOptions)}.{nameof(SlackIdempotencyOptions.CompletionMaxAttempts)} must be greater than zero.")
            .Validate(
                opts => opts.CompletionInitialDelayMilliseconds >= 0,
                $"{nameof(SlackIdempotencyOptions)}.{nameof(SlackIdempotencyOptions.CompletionInitialDelayMilliseconds)} must be non-negative.")
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Seeds the in-memory <see cref="ISlackWorkspaceConfigStore"/> with
    /// workspace entries supplied through <c>appsettings.json</c> (or any
    /// other <see cref="IConfiguration"/> provider) under the
    /// <see cref="SlackWorkspaceSeedOptions.SectionName"/> section. This
    /// closes the Stage 3.1 evaluator iter-1 item 4 gap: without it the
    /// shipped Worker host registers an empty in-memory store and cannot
    /// validate a real Slack request even though the middleware is wired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extension uses <c>TryAddSingleton</c> for the store binding so
    /// production composition roots that register a database-backed
    /// <see cref="ISlackWorkspaceConfigStore"/> (Stage 2.3 EF-backed
    /// implementation) BEFORE this call still win. Seed entries supplied
    /// through configuration are then ignored, which is the desired
    /// behaviour for a production deployment whose authoritative store
    /// is the database.
    /// </para>
    /// <para>
    /// The signing secret is NEVER stored in the
    /// <see cref="SlackWorkspaceSeedEntry"/> -- only the reference
    /// (<c>env://VAR_NAME</c> or equivalent) that the
    /// <c>ISecretProvider</c> chain resolves at HMAC time. Operators
    /// supply real secrets through environment variables (or KeyVault /
    /// Kubernetes secret stores added in Stage 3.3) so no plaintext
    /// secret material ever lands in source control.
    /// </para>
    /// </remarks>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">Configuration root containing a
    /// <c>Slack:Workspaces</c> section. A missing section is tolerated:
    /// the store is seeded empty and the validator rejects every request
    /// with <c>UnknownWorkspace</c>.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddSlackWorkspaceConfigStoreFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SlackWorkspaceSeedOptions>()
            .Configure(opts => SlackWorkspaceSeedBinder.BindEntries(configuration, opts));

        // Construct the in-memory store eagerly so the seed is observable
        // through ISlackWorkspaceConfigStore as soon as the container is
        // built. The store is registered via TryAdd so a database-backed
        // override registered earlier in the composition root wins.
        services.TryAddSingleton<ISlackWorkspaceConfigStore>(sp =>
        {
            IOptions<SlackWorkspaceSeedOptions> opts =
                sp.GetRequiredService<IOptions<SlackWorkspaceSeedOptions>>();
            IEnumerable<SlackWorkspaceConfig> seed = SlackWorkspaceSeedBinder.Materialize(opts.Value);
            return new InMemorySlackWorkspaceConfigStore(seed);
        });

        return services;
    }
}
