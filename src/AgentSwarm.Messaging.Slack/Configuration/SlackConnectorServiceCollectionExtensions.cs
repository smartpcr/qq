// -----------------------------------------------------------------------
// <copyright file="SlackConnectorServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Configuration;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public static IServiceCollection AddSlackConnector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectorSection = configuration.GetSection(SlackConnectorOptions.SectionName);
        var retrySection = configuration.GetSection(SlackRetryOptions.SectionName);

        services
            .AddOptions<SlackConnectorOptions>()
            .Bind(connectorSection)
            .ValidateDataAnnotations()
            .Validate(
                opts => opts.MaxWorkspaces > 0,
                $"{nameof(SlackConnectorOptions)}.{nameof(SlackConnectorOptions.MaxWorkspaces)} must be greater than zero.")
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

        return services;
    }
}
