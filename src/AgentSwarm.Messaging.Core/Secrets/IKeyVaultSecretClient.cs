// -----------------------------------------------------------------------
// <copyright file="KeyVaultSecretClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Thin delegate-shaped abstraction over an Azure Key Vault (or
/// compatible) secret lookup. The interface is intentionally tiny so
/// the Core assembly does not have to take a hard dependency on
/// <c>Azure.Security.KeyVault.Secrets</c>: the operator wires up a
/// concrete implementation via
/// <see cref="SecretProviderServiceCollectionExtensions.AddKeyVaultSecretProvider"/>
/// and may use the Azure SDK, a custom HTTP client, or any other
/// mechanism for the actual fetch.
/// </summary>
/// <remarks>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The decoupling here mirrors the
/// <see cref="EnvironmentSecretProvider"/> resolver-delegate pattern:
/// Core ships the URL-routing + caching + exception-mapping logic and
/// stays SDK-free, while production hosts plug in the real client at
/// the composition root.
/// </remarks>
public interface IKeyVaultSecretClient
{
    /// <summary>
    /// Returns the plain-text value for the named Key Vault secret, or
    /// <see langword="null"/> when the vault has no such entry.
    /// </summary>
    /// <param name="secretName">
    /// The Key Vault secret name (the portion of the reference URI
    /// after <see cref="KeyVaultSecretProvider.Scheme"/>).
    /// </param>
    /// <param name="ct">Cancellation token honoured by the network call.</param>
    Task<string?> GetSecretAsync(string secretName, CancellationToken ct);
}
