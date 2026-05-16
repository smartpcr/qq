// -----------------------------------------------------------------------
// <copyright file="CompositeSecretProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

/// <summary>
/// Routing <see cref="ISecretProvider"/> that selects an underlying
/// backend at construction time based on
/// <see cref="SecretProviderOptions.ProviderType"/>. Stage 3.1 ships the
/// <see cref="SecretProviderType.Environment"/> and
/// <see cref="SecretProviderType.InMemory"/> backends; Stage 3.3 layers
/// caching and Azure Key Vault on top of the same composition surface.
/// </summary>
/// <remarks>
/// The composite is a thin selector rather than a chain-of-responsibility
/// because production wiring only needs ONE backend per host: the
/// signing-secret reference scheme is fixed for the deployment. Tests
/// that need to combine backends do so by injecting the concrete
/// provider directly.
/// </remarks>
public sealed class CompositeSecretProvider : ISecretProvider
{
    private readonly ISecretProvider inner;

    /// <summary>
    /// Selects a backend based on
    /// <see cref="SecretProviderOptions.ProviderType"/>. Falls back to
    /// <see cref="EnvironmentSecretProvider"/> for any value not handled
    /// here so a typo in configuration cannot silently swap to an
    /// in-memory store in production.
    /// </summary>
    /// <param name="options">Snapshot of resolved options.</param>
    /// <param name="environmentProvider">
    /// Environment-variable backend, injected so tests can substitute a
    /// deterministic resolver.
    /// </param>
    /// <param name="inMemoryProvider">In-memory backend for tests.</param>
    public CompositeSecretProvider(
        IOptions<SecretProviderOptions> options,
        EnvironmentSecretProvider environmentProvider,
        InMemorySecretProvider inMemoryProvider)
    {
        this.inner = Select(options, environmentProvider, inMemoryProvider);
    }

    /// <inheritdoc />
    public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        => this.inner.GetSecretAsync(secretRef, ct);

    private static ISecretProvider Select(
        IOptions<SecretProviderOptions> options,
        EnvironmentSecretProvider environmentProvider,
        InMemorySecretProvider inMemoryProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environmentProvider);
        ArgumentNullException.ThrowIfNull(inMemoryProvider);

        SecretProviderOptions resolved = options.Value ?? new SecretProviderOptions();
        return resolved.ProviderType switch
        {
            SecretProviderType.InMemory => inMemoryProvider,
            SecretProviderType.Environment => environmentProvider,
            _ => environmentProvider,
        };
    }
}
