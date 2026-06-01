// -----------------------------------------------------------------------
// <copyright file="KeyVaultSecretProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// <see cref="ISecretProvider"/> that resolves <c>keyvault://{name}</c>
/// references via an injected <see cref="IKeyVaultSecretClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (architecture.md §7.3). The provider performs ONLY URL parsing,
/// validation, and <see cref="SecretNotFoundException"/> mapping;
/// the actual network call lives behind
/// <see cref="IKeyVaultSecretClient"/> so the Core assembly is free
/// of Azure-SDK dependencies. Production hosts register a real
/// client via
/// <see cref="SecretProviderServiceCollectionExtensions.AddKeyVaultSecretProvider"/>;
/// tests substitute a delegate-backed double.
/// </para>
/// <para>
/// The <see cref="Scheme"/> prefix is required: anything that does not
/// start with <c>keyvault://</c> raises
/// <see cref="ArgumentException"/> so a malformed appsettings
/// reference cannot silently fall through to the wrong backend.
/// </para>
/// </remarks>
public sealed class KeyVaultSecretProvider : ISecretProvider
{
    /// <summary>Reference-URI scheme this provider responds to.</summary>
    public const string Scheme = "keyvault://";

    private readonly IKeyVaultSecretClient client;

    /// <summary>Initializes a new instance bound to the supplied client.</summary>
    public KeyVaultSecretProvider(IKeyVaultSecretClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public async Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new ArgumentException(
                "Secret reference must be a non-empty, non-whitespace string.",
                nameof(secretRef));
        }

        if (!secretRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Key Vault references must start with '{Scheme}' (got '{secretRef}').",
                nameof(secretRef));
        }

        string name = secretRef[Scheme.Length..];
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                $"Key Vault reference '{secretRef}' did not yield a non-empty secret name.",
                nameof(secretRef));
        }

        ct.ThrowIfCancellationRequested();

        string? value;
        try
        {
            value = await this.client.GetSecretAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            // Wrap the underlying network / authentication failure so
            // callers get a uniform exception shape regardless of the
            // chosen client implementation, and so the secret ref
            // appears in the exception message for triage.
            throw new SecretNotFoundException(secretRef, ex);
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new SecretNotFoundException(secretRef);
        }

        return value;
    }
}
