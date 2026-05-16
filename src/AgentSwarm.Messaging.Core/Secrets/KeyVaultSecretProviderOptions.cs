// -----------------------------------------------------------------------
// <copyright file="KeyVaultSecretProviderOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

/// <summary>
/// Strongly-typed options for the <see cref="KeyVaultSecretProvider"/>.
/// Bound from the <see cref="SectionName"/>
/// (<c>SecretProvider:KeyVault</c>) section.
/// </summary>
/// <remarks>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The actual Key Vault HTTP client is supplied by the operator via
/// <see cref="SecretProviderServiceCollectionExtensions.AddKeyVaultSecretProvider"/>
/// so the Core assembly stays free of Azure-SDK dependencies; this
/// options bag captures only the operator-visible configuration
/// surface that does NOT require the SDK to materialize.
/// </remarks>
public sealed class KeyVaultSecretProviderOptions
{
    /// <summary>
    /// Configuration section name (<c>SecretProvider:KeyVault</c>).
    /// </summary>
    public const string SectionName = "SecretProvider:KeyVault";

    /// <summary>
    /// Optional Key Vault URI (e.g.,
    /// <c>https://my-vault.vault.azure.net</c>). Stored on the options
    /// so an operator-facing diagnostics endpoint can surface "which
    /// vault are we resolving against" without taking a runtime
    /// dependency on the Azure SDK type. The actual lookup delegate
    /// supplied to
    /// <see cref="SecretProviderServiceCollectionExtensions.AddKeyVaultSecretProvider"/>
    /// is free to ignore this value if it has another way to find the
    /// vault (e.g., an injected <c>SecretClient</c>).
    /// </summary>
    public string? VaultUri { get; set; }
}
