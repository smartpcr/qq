// -----------------------------------------------------------------------
// <copyright file="TelegramSecretSourceValidator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Worker.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 5.1, step 4 — startup validation that fails the host with a
/// clear, operator-facing error when no <i>approved</i> secret source
/// has supplied the Telegram bot token. Approved sources are Azure
/// Key Vault, .NET User Secrets, environment variables, and (for
/// integration tests) the in-memory configuration provider. The
/// validator REJECTS plaintext file sources such as
/// <c>appsettings.json</c> with a Warning log line and an
/// <see cref="InvalidOperationException"/>, even when those files
/// happen to carry a non-blank value, because storing the bot token
/// in a checked-in configuration file is a security incident per
/// docs/stories/qq-TELEGRAM-MESSENGER-S/dev-setup.md and the
/// Stage 5.1 brief.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why run this in addition to <c>TelegramOptionsValidator</c>.</b>
/// The options validator runs during <c>IHost.StartAsync</c> via the
/// <c>ValidateOnStart()</c> hook registered by
/// <see cref="AgentSwarm.Messaging.Telegram.TelegramServiceCollectionExtensions.AddTelegram"/>
/// and already produces an <c>OptionsValidationException</c> on a
/// blank <see cref="AgentSwarm.Messaging.Telegram.TelegramOptions.BotToken"/>.
/// That covers half the brief — "fail when missing" — but never
/// catches the second failure mode: a token supplied by an
/// unapproved source (the operator pasted the token into
/// appsettings.json "just for a quick test"). Stage 5.1 explicitly
/// mandates source-strict validation: only Key Vault, User Secrets,
/// and environment variables are acceptable in production. This
/// validator runs BEFORE <c>app.Run()</c> so it can emit a
/// Warning-level diagnostic naming the offending provider and
/// throw with a tightly worded error before the host accepts any
/// traffic.
/// </para>
/// <para>
/// <b>Never logs the token.</b> The validator examines presence /
/// absence and provider identity per source — it never logs the
/// secret value. A populated, source-approved token causes the
/// validator to emit an Information line that mentions only the
/// winning provider's type name.
/// </para>
/// </remarks>
public static class TelegramSecretSourceValidator
{
    /// <summary>
    /// Configuration key the validator inspects. Matches the constant
    /// on <see cref="TelegramKeyVaultSecretManager"/> so a future
    /// rename surfaces as a compile-break, not as a silent regression.
    /// </summary>
    public const string BotTokenConfigurationKey =
        TelegramKeyVaultSecretManager.BotTokenConfigurationKey;

    /// <summary>
    /// Environment variable form of the bot-token configuration key
    /// (the <c>:</c> separator is encoded as <c>__</c> per the .NET
    /// configuration convention).
    /// </summary>
    public const string BotTokenEnvironmentVariableName = "Telegram__BotToken";

    /// <summary>
    /// Fully-qualified type names of the configuration providers
    /// that may legitimately supply a secret. Memory provider is
    /// included so integration tests (and intentional in-process
    /// injection) work; everything else is rejected.
    /// </summary>
    private static readonly HashSet<string> ApprovedProviderTypeNames = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.Configuration.Memory.MemoryConfigurationProvider",
        "Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider",
        "Azure.Extensions.AspNetCore.Configuration.Secrets.AzureKeyVaultConfigurationProvider",
    };

    /// <summary>
    /// Canonical path segment that .NET User Secrets writes under on
    /// Windows: <c>%APPDATA%\Microsoft\UserSecrets\&lt;id&gt;\secrets.json</c>.
    /// Stored with forward slashes so it can be compared after
    /// separator normalization. NTFS is case-insensitive, so this
    /// segment is matched <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    private const string WindowsUserSecretsSegment = "/Microsoft/UserSecrets/";

    /// <summary>
    /// Canonical path segment that .NET User Secrets writes under on
    /// Linux/macOS: <c>$HOME/.microsoft/usersecrets/&lt;id&gt;/secrets.json</c>.
    /// <c>PathHelper</c> hard-codes the lowercase form; matched with
    /// <see cref="StringComparison.Ordinal"/> because those file
    /// systems are typically case-sensitive and the canonical
    /// produced casing is always lowercase.
    /// </summary>
    private const string UnixUserSecretsSegment = "/.microsoft/usersecrets/";

    /// <summary>
    /// File name <c>Microsoft.Extensions.Configuration.UserSecrets.PathHelper</c>
    /// always uses for the secrets store. Required (in addition to
    /// the canonical directory segment) so that an unrelated JSON
    /// file under a similarly-named directory is not approved.
    /// </summary>
    private const string UserSecretsFileName = "secrets.json";

    /// <summary>
    /// Maximum number of nested <see cref="ChainedConfigurationProvider"/>
    /// layers we will recursively unwrap when classifying the
    /// effective source. Bounded so a pathological / cyclic
    /// configuration graph cannot stack-overflow the validator.
    /// In practice WebApplicationBuilder produces at most two
    /// layers (app-config chains host-config); the limit is set
    /// generously above that.
    /// </summary>
    private const int MaxChainedProviderUnwrapDepth = 8;

    /// <summary>
    /// Validates that <see cref="BotTokenConfigurationKey"/> was
    /// supplied by an APPROVED configuration source (Azure Key Vault,
    /// .NET User Secrets, environment variables, or the in-memory
    /// provider used by integration tests). Emits a Warning
    /// diagnostic and throws on both failure modes: (a) the key is
    /// blank everywhere, and (b) the key was supplied but by an
    /// unapproved source such as <c>appsettings.json</c>.
    /// </summary>
    /// <param name="configuration">The host's effective
    /// <see cref="IConfiguration"/> (post-Build). Must include every
    /// provider the host was bootstrapped with — Key Vault, User
    /// Secrets, environment variables, JSON files, in-memory.</param>
    /// <param name="environment">The host environment so the Warning
    /// log can tell the operator whether User Secrets was even
    /// supposed to be active (only Development environments
    /// auto-register the provider).</param>
    /// <param name="keyVaultUri">The configured
    /// <c>KeyVault:Uri</c> value, or <c>null</c> / blank when Key
    /// Vault was not configured. Used purely as a diagnostic flag so
    /// the Warning log line can report whether the Key Vault
    /// provider was wired in.</param>
    /// <param name="logger">Logger used to emit the diagnostic line
    /// at Warning level when the token is missing or unapproved and
    /// at Information level when it is present and approved. The
    /// logger category is the caller's choice (<c>Program</c> is
    /// fine).</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when (a) the bot token is blank in every configuration
    /// provider, OR (b) the bot token was supplied by an unapproved
    /// source. The message names the offending provider (case b) or
    /// lists each inspected source and its verdict (case a), and
    /// ends with the exact remediation command an operator should
    /// run.
    /// </exception>
    public static void EnsureBotTokenConfigured(
        IConfiguration configuration,
        IHostEnvironment environment,
        string? keyVaultUri,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        var tokenValue = configuration[BotTokenConfigurationKey];
        if (string.IsNullOrWhiteSpace(tokenValue))
        {
            ThrowMissingTokenDiagnostic(environment, keyVaultUri, logger);
        }

        // Token IS present somewhere — now make sure the supplying
        // provider is approved. JsonConfigurationProvider for the
        // User Secrets path is approved; JsonConfigurationProvider
        // for appsettings.json is REJECTED.
        var winningProvider = ResolveSupplyingProvider(configuration);
        var sourceClassification = ClassifyProvider(winningProvider);
        if (!sourceClassification.IsApproved)
        {
            logger.LogWarning(
                "Telegram:BotToken was supplied by an unapproved configuration source: {ProviderDescription}. "
                + "Stage 5.1 requires the bot token to come from Azure Key Vault, .NET User Secrets, or an environment variable. "
                + "Storing the token in {SourceLabel} is a security incident — the value is in plaintext on disk and may be committed to source control. "
                + "Remove it from that source and configure one of the approved sources (see docs/stories/qq-TELEGRAM-MESSENGER-S/dev-setup.md).",
                sourceClassification.Description,
                sourceClassification.SourceLabel);

            throw new InvalidOperationException(
                "Telegram bot token was supplied by an unapproved source: "
                + sourceClassification.Description
                + System.Environment.NewLine
                + "Stage 5.1 requires the token to be loaded from Azure Key Vault, .NET User Secrets, or an environment variable. "
                + "Plaintext file sources such as appsettings.json are rejected to prevent accidental commit of the bot token to source control."
                + System.Environment.NewLine
                + System.Environment.NewLine
                + "Remediation:" + System.Environment.NewLine
                + "  * Remove '" + BotTokenConfigurationKey
                + "' from the offending source (typically appsettings.json or appsettings.{Environment}.json)." + System.Environment.NewLine
                + "  * Production: set 'KeyVault:Uri' to your Azure Key Vault URI and create a secret named '"
                + TelegramKeyVaultSecretManager.BotTokenSecretName + "'." + System.Environment.NewLine
                + "  * Local development: run 'dotnet user-secrets set \"" + BotTokenConfigurationKey
                + "\" \"<your-bot-token>\"' from the AgentSwarm.Messaging.Worker project directory," + System.Environment.NewLine
                + "    or set the environment variable " + BotTokenEnvironmentVariableName + "." + System.Environment.NewLine
                + "See docs/stories/qq-TELEGRAM-MESSENGER-S/dev-setup.md for the full setup guide.");
        }

        logger.LogInformation(
            "Telegram:BotToken loaded from {Provider}. Token value is never logged.",
            sourceClassification.Description);
    }

    /// <summary>
    /// Best-effort identification of which configuration provider
    /// supplied the bot-token value. Public for unit-test coverage.
    /// Returns the matching <see cref="IConfigurationProvider"/> or
    /// <see langword="null"/> when the configuration is not an
    /// <see cref="IConfigurationRoot"/> or no provider supplies a
    /// non-blank value.
    /// </summary>
    public static IConfigurationProvider? ResolveSupplyingProvider(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return null;
        }

        // IConfigurationRoot.Providers iterates in registration order;
        // the LAST provider to return a non-null value wins. Walk the
        // list in reverse so the first match is the effective source.
        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.TryGet(BotTokenConfigurationKey, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Best-effort identification of which configuration provider
    /// supplied the bot-token value, as a printable label. Public
    /// for backward compatibility with iter-2 tests and for the
    /// success-path Information log line.
    /// </summary>
    public static string DescribeWinningProvider(IConfiguration configuration)
    {
        var provider = ResolveSupplyingProvider(configuration);
        return ClassifyProvider(provider).Description;
    }

    /// <summary>
    /// Classifies a configuration provider as approved or unapproved
    /// for carrying the bot token. The approval set is the brief's
    /// "Key Vault / User Secrets / env vars" trio plus the in-memory
    /// provider for integration testing. Public for unit-test
    /// coverage so a future provider rename surfaces immediately.
    /// </summary>
    /// <param name="provider">The provider that supplied the bot
    /// token, or <see langword="null"/> when no provider supplied a
    /// value.</param>
    /// <returns>A classification record describing whether the
    /// provider is approved, a human-readable description, and a
    /// short label suitable for use in the Warning log message.</returns>
    public static ProviderClassification ClassifyProvider(IConfigurationProvider? provider)
    {
        return ClassifyProvider(provider, depth: 0);
    }

    /// <summary>
    /// Recursive overload of <see cref="ClassifyProvider(IConfigurationProvider?)"/>
    /// used internally when unwrapping nested
    /// <see cref="ChainedConfigurationProvider"/> layers. The
    /// <paramref name="depth"/> counter guards against pathological
    /// graphs that could otherwise blow the stack.
    /// </summary>
    private static ProviderClassification ClassifyProvider(IConfigurationProvider? provider, int depth)
    {
        if (provider is null)
        {
            return new ProviderClassification(
                IsApproved: false,
                Description: "an unidentified configuration provider",
                SourceLabel: "unknown");
        }

        var typeName = provider.GetType().FullName ?? string.Empty;

        // Approved-by-type-name: Memory, EnvironmentVariables,
        // AzureKeyVault. These never carry plaintext-on-disk risk.
        if (ApprovedProviderTypeNames.Contains(typeName))
        {
            return new ProviderClassification(
                IsApproved: true,
                Description: provider.GetType().Name,
                SourceLabel: provider.GetType().Name);
        }

        // File-based providers (JSON, XML, INI) need a path check:
        // User Secrets is also a JsonConfigurationProvider but its
        // file path lives under the per-user secrets directory laid
        // out by Microsoft.Extensions.Configuration.UserSecrets.PathHelper.
        // Allowlist ONLY the canonical User Secrets layout — both
        // the file name (secrets.json) and a canonical parent segment
        // (\Microsoft\UserSecrets\ on Windows, /.microsoft/usersecrets/
        // on Linux/macOS) must match. A loose "Contains('usersecrets')"
        // check would silently approve, e.g., an appsettings.json
        // that happens to live in a project subfolder named
        // "usersecrets/", so we deliberately reject anything that
        // doesn't match the documented PathHelper output.
        if (provider is FileConfigurationProvider fileProvider)
        {
            var physicalPath = TryResolvePhysicalPath(fileProvider);
            if (physicalPath is not null && IsCanonicalUserSecretsPath(physicalPath))
            {
                return new ProviderClassification(
                    IsApproved: true,
                    Description: ".NET User Secrets (" + Path.GetFileName(physicalPath) + ")",
                    SourceLabel: "UserSecrets");
            }

            var description = physicalPath is not null
                ? provider.GetType().Name + " (" + Path.GetFileName(physicalPath) + ")"
                : provider.GetType().Name;
            return new ProviderClassification(
                IsApproved: false,
                Description: description,
                SourceLabel: physicalPath is not null ? Path.GetFileName(physicalPath) : provider.GetType().Name);
        }

        // ChainedConfigurationProvider wraps another IConfiguration
        // and forwards all reads to it. Earlier iterations of this
        // validator approved it unconditionally on the assumption
        // that WebApplicationBuilder only chains "safe" sources
        // (env vars + command-line + inner-host config), but that
        // assumption is unsound: a caller can chain ANY
        // IConfiguration — including one built from appsettings.json
        // — and a blanket approval would silently bypass the
        // source-strict policy. ChainedConfigurationProvider exposes
        // its inner IConfiguration via a PUBLIC property in .NET 6+,
        // so we unwrap, re-resolve the inner provider that actually
        // supplies the bot-token key, and recursively classify THAT
        // provider. If the inner chain is opaque (not an
        // IConfigurationRoot, or no inner provider supplies the
        // key, or recursion exceeds MaxChainedProviderUnwrapDepth),
        // we REJECT conservatively rather than approve a source we
        // cannot prove safe.
        if (provider is ChainedConfigurationProvider chained)
        {
            return ClassifyChainedProvider(chained, depth);
        }

        return new ProviderClassification(
            IsApproved: false,
            Description: provider.GetType().Name,
            SourceLabel: provider.GetType().Name);
    }

    /// <summary>
    /// Unwraps a <see cref="ChainedConfigurationProvider"/>, finds
    /// which inner provider actually supplies
    /// <see cref="BotTokenConfigurationKey"/> (using the same
    /// last-writer-wins precedence as
    /// <see cref="ResolveSupplyingProvider"/>), and classifies that
    /// inner provider recursively. Returns a REJECTED classification
    /// whenever the inner chain cannot be inspected or the inner
    /// supplier is itself unapproved — chained providers are only
    /// approved when the underlying source is independently approved.
    /// </summary>
    private static ProviderClassification ClassifyChainedProvider(
        ChainedConfigurationProvider chained,
        int depth)
    {
        if (depth >= MaxChainedProviderUnwrapDepth)
        {
            return new ProviderClassification(
                IsApproved: false,
                Description: "ChainedConfigurationProvider (unwrap depth limit reached; cannot verify inner source)",
                SourceLabel: "ChainedTooDeep");
        }

        if (chained.Configuration is not IConfigurationRoot innerRoot)
        {
            // The inner IConfiguration is not an IConfigurationRoot,
            // which means we cannot enumerate its providers. The
            // bot-token value could be coming from an arbitrary
            // (possibly plaintext-on-disk) source we cannot see —
            // refuse to approve.
            return new ProviderClassification(
                IsApproved: false,
                Description: "ChainedConfigurationProvider (inner configuration is opaque; cannot verify source)",
                SourceLabel: "ChainedOpaque");
        }

        IConfigurationProvider? innerWinning = null;
        foreach (var inner in innerRoot.Providers.Reverse())
        {
            if (inner.TryGet(BotTokenConfigurationKey, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                innerWinning = inner;
                break;
            }
        }

        if (innerWinning is null)
        {
            // The chain says it supplies the key (otherwise we would
            // not have reached this code path) but no inner provider
            // claims ownership. That implies a custom IConfiguration
            // implementation synthesising values; treat as unverifiable.
            return new ProviderClassification(
                IsApproved: false,
                Description: "ChainedConfigurationProvider (no inner provider claims the key; cannot verify source)",
                SourceLabel: "ChainedNoSource");
        }

        var innerClassification = ClassifyProvider(innerWinning, depth + 1);

        // Preserve the inner verdict (approved iff inner is approved)
        // and prefix the description / label so operators can see the
        // value reached them through a chain. Example renderings:
        //   "ChainedConfigurationProvider → EnvironmentVariablesConfigurationProvider"  (approved)
        //   "ChainedConfigurationProvider → JsonConfigurationProvider (appsettings.json)"  (rejected)
        return new ProviderClassification(
            IsApproved: innerClassification.IsApproved,
            Description: "ChainedConfigurationProvider → " + innerClassification.Description,
            SourceLabel: "Chained:" + innerClassification.SourceLabel);
    }

    /// <summary>
    /// Determines whether a JSON configuration file's physical path
    /// matches the canonical .NET User Secrets layout produced by
    /// <c>Microsoft.Extensions.Configuration.UserSecrets.PathHelper</c>.
    /// That helper writes secrets to one of two well-defined
    /// locations:
    /// <list type="bullet">
    ///   <item><description>Windows: <c>%APPDATA%\Microsoft\UserSecrets\&lt;UserSecretsId&gt;\secrets.json</c></description></item>
    ///   <item><description>Linux/macOS: <c>$HOME/.microsoft/usersecrets/&lt;UserSecretsId&gt;/secrets.json</c></description></item>
    /// </list>
    /// The match is segment-based (not a loose substring check) and
    /// also requires the literal filename <c>secrets.json</c>, so
    /// that an unrelated project folder named <c>usersecrets/</c>
    /// (or any path containing the substring) holding an
    /// <c>appsettings.json</c> is NOT silently approved as a User
    /// Secrets source. Public for unit-test coverage.
    /// </summary>
    /// <param name="physicalPath">Absolute path to the JSON file that
    /// backs the <see cref="FileConfigurationProvider"/>.</param>
    /// <returns><see langword="true"/> when the path is a canonical
    /// User Secrets store; <see langword="false"/> otherwise.</returns>
    public static bool IsCanonicalUserSecretsPath(string? physicalPath)
    {
        if (string.IsNullOrEmpty(physicalPath))
        {
            return false;
        }

        // PathHelper always writes the file literally as
        // 'secrets.json'. Accept any casing because NTFS is case-
        // insensitive (and the file is always created as lowercase
        // on case-sensitive file systems).
        var fileName = Path.GetFileName(physicalPath);
        if (!string.Equals(fileName, UserSecretsFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Normalize separators so the same substring tests work for
        // both Windows ('\') and *nix ('/') paths.
        var normalized = physicalPath.Replace('\\', '/');

        // Windows canonical segment under %APPDATA%. NTFS is case-
        // insensitive, so compare with OrdinalIgnoreCase.
        if (normalized.Contains(WindowsUserSecretsSegment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Linux/macOS canonical segment under $HOME. PathHelper
        // hard-codes the lowercase form; those file systems are
        // typically case-sensitive, so compare with Ordinal.
        if (normalized.Contains(UnixUserSecretsSegment, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Static description of each candidate source so the Warning log
    /// line and the exception message can present a uniform table to
    /// the operator. Public for unit-test coverage.
    /// </summary>
    public static IReadOnlyList<SourceInspection> InspectSources(
        IHostEnvironment environment,
        string? keyVaultUri)
    {
        var keyVaultConfigured = !string.IsNullOrWhiteSpace(keyVaultUri);
        var keyVaultUriValid = keyVaultConfigured
            && Uri.TryCreate(keyVaultUri, UriKind.Absolute, out _);

        var envVar = System.Environment.GetEnvironmentVariable(BotTokenEnvironmentVariableName);
        var envVarSet = !string.IsNullOrWhiteSpace(envVar);

        return new List<SourceInspection>
        {
            new(
                "Azure Key Vault (KeyVault:Uri)",
                keyVaultConfigured
                    ? (keyVaultUriValid
                        ? "Configured (URI present) but did not provide '"
                          + BotTokenConfigurationKey
                          + "' (vault missing secret '"
                          + TelegramKeyVaultSecretManager.BotTokenSecretName
                          + "', or the configured credential lacks 'Get'/'List' permission)"
                        : "Misconfigured: 'KeyVault:Uri' is set but is not a valid absolute URI")
                    : "Not configured (set 'KeyVault:Uri' to your vault's URI to enable)"),
            new(
                ".NET User Secrets",
                environment.IsDevelopment()
                    ? "Active (Development environment) but did not provide '"
                      + BotTokenConfigurationKey
                      + "' — run 'dotnet user-secrets set \""
                      + BotTokenConfigurationKey
                      + "\" \"<token>\"' from the Worker project directory"
                    : "Inactive (User Secrets is only auto-registered when ASPNETCORE_ENVIRONMENT=Development; current environment is '"
                      + environment.EnvironmentName
                      + "')"),
            new(
                "Environment variable '" + BotTokenEnvironmentVariableName + "'",
                envVarSet
                    // The env var IS non-blank in the current process, yet
                    // the resolved configuration key still came back blank
                    // (otherwise the caller — ThrowMissingTokenDiagnostic —
                    // would never have run). In practice that means a
                    // later-registered provider supplied a blank value for
                    // the same key and won the last-writer-wins precedence
                    // (e.g., a blank entry in appsettings.json layered on
                    // top of EnvironmentVariablesConfigurationProvider), or
                    // the env var was set in the parent shell *after* the
                    // host built its configuration. Either way, the env var
                    // itself is fine — the value just didn't reach the
                    // resolved key, so steer the operator toward the right
                    // place to look instead of the previous (misleading)
                    // "rejected as blank" framing.
                    ? "Set (non-blank), but did not reach the resolved '"
                      + BotTokenConfigurationKey
                      + "' value — likely overridden by a higher-priority configuration provider "
                      + "(e.g., a blank '"
                      + BotTokenConfigurationKey
                      + "' entry in appsettings.json / appsettings.{Environment}.json), "
                      + "or the variable was exported after the host built its configuration"
                    : "Not set"),
            new(
                "appsettings.json / appsettings.{Environment}.json",
                "REJECTED: plaintext file sources are not approved for the bot token even if the value is present (Stage 5.1 security policy)"),
        };
    }

    /// <summary>
    /// Tuple-shaped record used purely for the diagnostic rendering;
    /// public so unit tests can assert on the contents directly.
    /// </summary>
    /// <param name="Source">Human-readable source label.</param>
    /// <param name="Verdict">Per-source diagnostic explanation.</param>
    public readonly record struct SourceInspection(string Source, string Verdict);

    /// <summary>
    /// Result of classifying a configuration provider as approved /
    /// unapproved for carrying secrets. Public so tests can assert on
    /// the classification logic directly.
    /// </summary>
    /// <param name="IsApproved">Whether this provider may legitimately
    /// supply <see cref="BotTokenConfigurationKey"/>.</param>
    /// <param name="Description">Printable description for diagnostic
    /// log lines and exception messages.</param>
    /// <param name="SourceLabel">Short token suitable for structured-
    /// logging keys (no spaces).</param>
    public readonly record struct ProviderClassification(
        bool IsApproved,
        string Description,
        string SourceLabel);

    private static string? TryResolvePhysicalPath(FileConfigurationProvider provider)
    {
        try
        {
            var source = provider.Source;
            if (source is null || string.IsNullOrEmpty(source.Path))
            {
                return null;
            }

            var fileProvider = source.FileProvider;
            if (fileProvider is null)
            {
                return source.Path;
            }

            var fileInfo = fileProvider.GetFileInfo(source.Path);
            return fileInfo?.PhysicalPath ?? source.Path;
        }
        catch
        {
            return null;
        }
    }

    private static void ThrowMissingTokenDiagnostic(
        IHostEnvironment environment,
        string? keyVaultUri,
        ILogger logger)
    {
        var sources = InspectSources(environment, keyVaultUri);
        var renderedSources = string.Join(
            System.Environment.NewLine,
            sources.Select(s => $"  - {s.Source}: {s.Verdict}"));

        logger.LogWarning(
            "Telegram:BotToken is not set by any configured secret source. Inspected sources:\n{Sources}",
            renderedSources);

        throw new InvalidOperationException(
            "Telegram bot token is not configured. The Worker cannot start without a value for "
            + BotTokenConfigurationKey + ". Inspected sources:\n"
            + renderedSources
            + System.Environment.NewLine
            + System.Environment.NewLine
            + "Remediation:\n"
            + "  * Production: set the 'KeyVault:Uri' configuration value to your Azure Key Vault URI "
            + "and create a secret named '" + TelegramKeyVaultSecretManager.BotTokenSecretName + "' "
            + "holding the bot token.\n"
            + "  * Local development: run "
            + "'dotnet user-secrets set \"" + BotTokenConfigurationKey + "\" \"<your-bot-token>\"' "
            + "from the AgentSwarm.Messaging.Worker project directory, or set the environment variable "
            + BotTokenEnvironmentVariableName + ".\n"
            + "See docs/stories/qq-TELEGRAM-MESSENGER-S/dev-setup.md for the full setup guide.");
    }
}
