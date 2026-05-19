// -----------------------------------------------------------------------
// <copyright file="TelegramKeyVaultSecretManager.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Worker.Configuration;

using System.Collections.Generic;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

/// <summary>
/// Stage 5.1 — <see cref="KeyVaultSecretManager"/> implementation that
/// maps a small, deterministic set of Azure Key Vault secret names onto
/// the configuration keys consumed by
/// <see cref="AgentSwarm.Messaging.Telegram.TelegramOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a custom <see cref="KeyVaultSecretManager"/>.</b> Azure Key
/// Vault secret names cannot contain the <c>:</c> character that the
/// .NET configuration system uses as a section separator. The default
/// <see cref="KeyVaultSecretManager.GetKey(KeyVaultSecret)"/>
/// implementation translates <c>--</c> in a secret name to <c>:</c>
/// (so <c>Telegram--BotToken</c> → <c>Telegram:BotToken</c>), which
/// would force operators to create vault secrets whose names encode
/// configuration shape. The implementation-plan brief (Stage 5.1,
/// step 2) requires a flat secret name — <c>TelegramBotToken</c> —
/// to map to <c>Telegram:BotToken</c>; this manager performs that
/// explicit mapping.
/// </para>
/// <para>
/// <b>Allowlist semantics.</b> The manager publishes ONLY the secrets
/// listed in <see cref="SecretNameMappings"/>. Any other secret in the
/// vault is ignored — neither loaded nor mapped — so a vault shared
/// across services does not silently bleed unrelated secrets into the
/// process configuration. This is defense-in-depth against the failure
/// mode where vault contents change over time and an unmapped name
/// suddenly starts overriding an in-process configuration value.
/// </para>
/// <para>
/// <b>Extensibility.</b> Additional mappings (for example
/// <c>TelegramSecretToken</c> → <c>Telegram:SecretToken</c> when the
/// webhook secret is promoted into Key Vault) are added by appending
/// to <see cref="SecretNameMappings"/>. Unit tests pin the existing
/// mappings exactly so an accidental rename surfaces immediately.
/// </para>
/// </remarks>
public sealed class TelegramKeyVaultSecretManager : KeyVaultSecretManager
{
    /// <summary>
    /// Vault secret name used by the Telegram bot token, per
    /// implementation-plan.md Stage 5.1 step 2.
    /// </summary>
    public const string BotTokenSecretName = "TelegramBotToken";

    /// <summary>
    /// Configuration key (using <c>:</c> separator) the bot token must
    /// land under so
    /// <see cref="AgentSwarm.Messaging.Telegram.TelegramOptions.BotToken"/>
    /// binds it.
    /// </summary>
    public const string BotTokenConfigurationKey = "Telegram:BotToken";

    /// <summary>
    /// Vault secret name used by the Telegram webhook secret token,
    /// per architecture.md §7.1 lines 1018–1021. Optional — the
    /// Stage 5.1 brief only mandates the bot-token mapping; this entry
    /// is included so a future deployment that promotes the webhook
    /// secret token into Key Vault does not need to ship another patch.
    /// </summary>
    public const string SecretTokenSecretName = "TelegramSecretToken";

    /// <summary>
    /// Configuration key the webhook secret-token lands under so
    /// <see cref="AgentSwarm.Messaging.Telegram.TelegramOptions.SecretToken"/>
    /// binds it.
    /// </summary>
    public const string SecretTokenConfigurationKey = "Telegram:SecretToken";

    /// <summary>
    /// Fixed mapping from Key Vault secret names to .NET configuration
    /// keys. Case-insensitive on the secret name because Key Vault
    /// treats secret names as case-insensitive (per the Azure REST
    /// contract). The configuration key is emitted verbatim.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SecretNameMappings =
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            [BotTokenSecretName] = BotTokenConfigurationKey,
            [SecretTokenSecretName] = SecretTokenConfigurationKey,
        };

    /// <inheritdoc />
    public override bool Load(SecretProperties secret)
    {
        if (secret is null || string.IsNullOrWhiteSpace(secret.Name))
        {
            return false;
        }

        return SecretNameMappings.ContainsKey(secret.Name);
    }

    /// <inheritdoc />
    public override string GetKey(KeyVaultSecret secret)
    {
        // The framework only invokes GetKey after Load returned true,
        // so secret.Name is guaranteed to be in the allowlist. Use
        // TryGetValue defensively anyway: if the manager is ever
        // reused outside the framework's documented call order, fall
        // back to the default base behaviour rather than throwing.
        if (secret is not null
            && !string.IsNullOrWhiteSpace(secret.Name)
            && SecretNameMappings.TryGetValue(secret.Name, out var configurationKey))
        {
            return configurationKey;
        }

        return base.GetKey(secret!);
    }
}
