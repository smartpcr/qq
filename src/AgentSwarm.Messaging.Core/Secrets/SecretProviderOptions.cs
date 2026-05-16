// -----------------------------------------------------------------------
// <copyright file="SecretProviderOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

/// <summary>
/// Strongly-typed configuration for the composite
/// <see cref="ISecretProvider"/> chosen at runtime. Bound from the
/// <c>SecretProvider</c> configuration section by
/// <see cref="SecretProviderServiceCollectionExtensions.AddSecretProvider"/>.
/// </summary>
/// <remarks>
/// Implementation-plan §Stage 3.1 / §Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Stage 3.1 wires the <see cref="SecretProviderType.Environment"/> and
/// <see cref="SecretProviderType.InMemory"/> backends; Stage 3.3 extends
/// the enum with <c>KeyVault</c> and <c>Kubernetes</c> by registering
/// additional <see cref="ISecretProvider"/> implementations against the
/// same options.
/// </remarks>
public sealed class SecretProviderOptions
{
    /// <summary>
    /// Configuration section name (<c>"SecretProvider"</c>) the options
    /// are bound from.
    /// </summary>
    public const string SectionName = "SecretProvider";

    /// <summary>
    /// Backend selector. Defaults to
    /// <see cref="SecretProviderType.Environment"/> so a freshly
    /// scaffolded host can resolve <c>env://VAR_NAME</c> references
    /// without additional configuration.
    /// </summary>
    public SecretProviderType ProviderType { get; set; } = SecretProviderType.Environment;

    /// <summary>
    /// Refresh interval (in minutes) for the future caching composite
    /// added by Stage 3.3. Stage 3.1 binds the value so the appsettings
    /// surface is stable across stages.
    /// </summary>
    public int RefreshIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Discriminator for the secret-provider backend selected by
/// <see cref="SecretProviderOptions.ProviderType"/>.
/// </summary>
public enum SecretProviderType
{
    /// <summary>
    /// Resolves <c>env://VAR_NAME</c> references against process
    /// environment variables. The Stage 3.1 default; suitable for CI,
    /// container, and developer workstations.
    /// </summary>
    Environment = 0,

    /// <summary>
    /// Resolves references from an in-process dictionary. Reserved for
    /// unit and integration tests; not selectable from configuration
    /// in production deployments.
    /// </summary>
    InMemory = 1,

    /// <summary>
    /// Azure Key Vault backend, wired by Stage 3.3.
    /// </summary>
    KeyVault = 2,

    /// <summary>
    /// Kubernetes-mounted secret backend, wired by Stage 3.3.
    /// </summary>
    Kubernetes = 3,
}
