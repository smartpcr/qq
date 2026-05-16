// -----------------------------------------------------------------------
// <copyright file="IKeyVaultSecretClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Abstraction over an Azure Key Vault secret client, allowing the secret
/// provider integration to retrieve messenger credentials without taking a
/// hard dependency on the concrete Azure SDK types.
/// </summary>
public interface IKeyVaultSecretClient
{
    /// <summary>
    /// Retrieves the current value of the named secret from Key Vault.
    /// </summary>
    /// <param name="secretName">The name of the secret to retrieve.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The plaintext secret value, or <see langword="null"/> when the secret is not present.</returns>
    Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default);
}
