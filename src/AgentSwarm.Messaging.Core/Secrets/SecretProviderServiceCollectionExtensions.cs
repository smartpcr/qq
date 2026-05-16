// -----------------------------------------------------------------------
// <copyright file="SecretProviderServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extensions that register the Stage 3.1 secret-resolution chain:
/// <see cref="EnvironmentSecretProvider"/> +
/// <see cref="InMemorySecretProvider"/> behind a
/// <see cref="CompositeSecretProvider"/> selected by
/// <see cref="SecretProviderOptions.ProviderType"/>.
/// </summary>
public static class SecretProviderServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="SecretProviderOptions"/> from the
    /// <c>SecretProvider</c> configuration section and registers an
    /// <see cref="ISecretProvider"/> implementation that routes through
    /// the configured backend.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">
    /// Configuration root containing a <c>SecretProvider</c> section. A
    /// missing section is tolerated: the options fall back to their
    /// defaults (<see cref="SecretProviderType.Environment"/>).
    /// </param>
    public static IServiceCollection AddSecretProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SecretProviderOptions>()
            .Bind(configuration.GetSection(SecretProviderOptions.SectionName))
            .Validate(
                opts => opts.RefreshIntervalMinutes >= 0,
                $"{nameof(SecretProviderOptions)}.{nameof(SecretProviderOptions.RefreshIntervalMinutes)} must be non-negative.")
            .ValidateOnStart();

        services.TryAddSingleton<EnvironmentSecretProvider>();
        services.TryAddSingleton<InMemorySecretProvider>();

        // CompositeSecretProvider becomes the canonical ISecretProvider
        // registration. Callers that need to substitute a custom provider
        // (e.g., the Stage 3.3 KeyVault provider) register their own
        // ISecretProvider BEFORE this call -- TryAddSingleton skips the
        // composite in that case.
        services.TryAddSingleton<ISecretProvider, CompositeSecretProvider>();

        return services;
    }
}
