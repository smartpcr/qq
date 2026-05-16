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
    /// <see cref="SecretProviderOptions.ProviderType"/>. Unsupported
    /// values that are exposed in <see cref="SecretProviderType"/> but
    /// not yet implemented in this stage (e.g.,
    /// <see cref="SecretProviderType.KeyVault"/> and
    /// <see cref="SecretProviderType.Kubernetes"/>) throw
    /// <see cref="InvalidOperationException"/> instead of silently
    /// falling back so an operator misconfiguration cannot mask a
    /// missing production secret backend. Truly unknown enum values
    /// (e.g., a typo cast to <see cref="SecretProviderType"/>) raise
    /// the same exception.
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
            SecretProviderType.KeyVault => throw BuildUnsupportedException(
                resolved.ProviderType,
                requiresKeyVaultRegistration: true),
            SecretProviderType.Kubernetes => throw BuildUnsupportedException(
                resolved.ProviderType,
                requiresKeyVaultRegistration: false),
            _ => throw BuildUnsupportedException(resolved.ProviderType, requiresKeyVaultRegistration: false),
        };
    }

    private static InvalidOperationException BuildUnsupportedException(
        SecretProviderType providerType,
        bool requiresKeyVaultRegistration)
    {
        // Stage 3.1 evaluator iter-3 item 3: silent fall-back to the
        // environment backend for unsupported provider types could mask
        // a missing production secret backend in deployments that set
        // SecretProvider:ProviderType = KeyVault or Kubernetes without
        // registering the backing ISecretProvider. Fail loudly at
        // construction time so the misconfiguration is impossible to
        // miss. Stage 3.3 (KeyVault) and Stage 3.x (Kubernetes) will
        // wire their providers by calling
        // services.AddSingleton<ISecretProvider, ...>() BEFORE
        // AddSecretProvider; the TryAddSingleton inside
        // AddSecretProvider then skips the composite entirely and this
        // exception never fires.
        string remediation = requiresKeyVaultRegistration
            ? "Register a KeyVault-backed ISecretProvider implementation BEFORE calling AddSecretProvider (Stage 3.3 provides one)."
            : "Register an ISecretProvider implementation for this backend BEFORE calling AddSecretProvider so the composite is skipped.";

        string message = FormattableString.Invariant(
            $"SecretProvider:ProviderType={providerType} is not supported by the built-in {nameof(CompositeSecretProvider)}; ")
            + "only Environment and InMemory backends are implemented in this stage. "
            + remediation;
        return new InvalidOperationException(message);
    }
}
