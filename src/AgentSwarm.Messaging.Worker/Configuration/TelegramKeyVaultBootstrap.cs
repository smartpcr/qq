// -----------------------------------------------------------------------
// <copyright file="TelegramKeyVaultBootstrap.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Worker.Configuration;

using System;
using System.Threading;
using Azure.Core;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Stage 5.1 — bootstrap helper that wires Azure Key Vault into the
/// host's configuration pipeline AND publishes a thin test seam
/// (<see cref="SecretClientFactoryOverride"/>) so the integration
/// suite can boot the actual Worker with <c>KeyVault:Uri</c>
/// configured against a stubbed <see cref="SecretClient"/>, instead
/// of needing real Azure credentials and a real vault.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a test seam exists.</b> The Stage 5.1 acceptance scenario
/// "<i>Given Key Vault contains TelegramBotToken, When the Worker
/// starts with Key Vault URI configured, Then
/// <see cref="AgentSwarm.Messaging.Telegram.TelegramOptions.BotToken"/>
/// is populated from Key Vault</i>" is only meaningful if it covers
/// the exact code path Program.cs runs — including
/// <c>WebApplication.CreateBuilder</c>, the
/// <see cref="AzureKeyVaultConfigurationExtensions.AddAzureKeyVault(IConfigurationBuilder, Uri, TokenCredential, AzureKeyVaultConfigurationOptions)"/>
/// call site, and the
/// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>
/// binding pipeline. Production traffic must hit Azure with a real
/// <see cref="SecretClient"/>; tests must NOT hit Azure but must
/// hit every other line. The pattern Microsoft uses across the
/// ASP.NET Core source (e.g. <c>StartupFilters</c>, internal
/// factories) is a static factory delegate guarded by
/// <see cref="AsyncLocal{T}"/> so the override propagates across
/// the test → <c>WebApplicationFactory&lt;Program&gt;</c> → host-
/// build async chain without becoming a cross-test data race.
/// </para>
/// <para>
/// <b>Why <see cref="AsyncLocal{T}"/> instead of a plain static
/// field.</b> xUnit runs tests in the same class serially by
/// default, but the test runner shares the assembly's static state
/// across all tests in a process. <see cref="AsyncLocal{T}"/> binds
/// the override to the logical call context: a test that sets it
/// inside a <c>using</c> block cannot leak the override to a
/// concurrently-scheduled test on another thread. The override is
/// installed via the public
/// <see cref="OverrideSecretClientFactory(System.Func{System.Uri, Azure.Core.TokenCredential, Azure.Security.KeyVault.Secrets.SecretClient})"/>
/// method which returns an <see cref="IDisposable"/> token — the
/// caller MUST dispose the token to restore the prior factory,
/// even on test failure (xUnit's <c>using</c> handles this).
/// </para>
/// <para>
/// <b>Behaviour when no override is registered.</b> The default
/// factory constructs a real <see cref="SecretClient"/> with
/// <see cref="DefaultAzureCredential"/>, exactly preserving the
/// production wiring. There is no behavioural difference between
/// the pre-iter-3 inlined construction and this indirection unless
/// a test overrides the factory.
/// </para>
/// </remarks>
public static class TelegramKeyVaultBootstrap
{
    private static readonly AsyncLocal<Func<Uri, TokenCredential, SecretClient>?> SecretClientFactoryOverride = new();
    private static readonly AsyncLocal<string?> KeyVaultUriOverrideValue = new();

    /// <summary>
    /// Returns the active <see cref="SecretClient"/> factory. When a
    /// test has called
    /// <see cref="OverrideSecretClientFactory(System.Func{System.Uri, Azure.Core.TokenCredential, Azure.Security.KeyVault.Secrets.SecretClient})"/>
    /// in the current logical call context, that override is
    /// returned; otherwise the default factory that constructs a
    /// real <see cref="SecretClient"/> wins.
    /// </summary>
    public static Func<Uri, TokenCredential, SecretClient> SecretClientFactory
        => SecretClientFactoryOverride.Value ?? DefaultFactory;

    /// <summary>
    /// Returns the active Key Vault URI override, or <see langword="null"/>
    /// when no test has called
    /// <see cref="OverrideKeyVaultUri(string)"/>. Production builds
    /// always observe <see langword="null"/> here; the property is
    /// public purely so tests can inspect the override without
    /// reaching into private state.
    /// </summary>
    public static string? KeyVaultUriOverride => KeyVaultUriOverrideValue.Value;

    /// <summary>
    /// Installs a test-supplied <see cref="SecretClient"/> factory
    /// in the current async call context. Returns an
    /// <see cref="IDisposable"/> token that restores the prior
    /// factory on disposal — call sites MUST dispose the token (the
    /// idiomatic pattern is a <c>using</c> declaration around the
    /// <c>WebApplicationFactory&lt;Program&gt;.CreateClient()</c>
    /// call). Re-entrant: nesting overrides is allowed; the
    /// outermost <c>Dispose</c> restores the original factory.
    /// </summary>
    /// <param name="factory">Factory that produces a stubbed
    /// <see cref="SecretClient"/> for the given vault URI and
    /// token credential. The credential is the one Program.cs
    /// constructed (<see cref="DefaultAzureCredential"/> in
    /// production); the factory typically ignores it because the
    /// stub does not authenticate.</param>
    /// <returns>Disposable scope guard — dispose to restore the
    /// previously-active factory.</returns>
    public static IDisposable OverrideSecretClientFactory(
        Func<Uri, TokenCredential, SecretClient> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var previous = SecretClientFactoryOverride.Value;
        SecretClientFactoryOverride.Value = factory;
        return new RestoreFactoryScope(previous);
    }

    /// <summary>
    /// Installs a test-supplied <c>KeyVault:Uri</c> value in the
    /// current async call context so
    /// <see cref="TryAddTelegramKeyVault(IConfigurationBuilder, IConfiguration)"/>
    /// will wire the Azure Key Vault provider EVEN WHEN the host's
    /// configuration pipeline has not yet surfaced the URI through
    /// <c>IConfiguration["KeyVault:Uri"]</c>. This is the seam that
    /// makes the Stage 5.1 brief's acceptance scenario
    /// "<i>When the Worker starts with Key Vault URI configured</i>"
    /// testable end-to-end through
    /// <c>WebApplicationFactory&lt;Program&gt;</c>: in that harness,
    /// the test's <c>ConfigureAppConfiguration</c> callback is
    /// registered AFTER Program.cs's own <c>ConfigureAppConfiguration</c>
    /// callback, so the test's in-memory <c>KeyVault:Uri</c> is
    /// invisible when the bootstrap callback fires. The
    /// <see cref="AsyncLocal{T}"/> override bypasses the callback-
    /// ordering trap by injecting the URI value at the same logical
    /// call-context layer as
    /// <see cref="OverrideSecretClientFactory(System.Func{System.Uri, Azure.Core.TokenCredential, Azure.Security.KeyVault.Secrets.SecretClient})"/>,
    /// so the two overrides compose cleanly in a single
    /// <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// Production never installs an override — the bootstrap reads
    /// <c>KeyVault:Uri</c> from <see cref="IConfiguration"/> the usual
    /// way and the AsyncLocal value is left at <see langword="null"/>.
    /// The override is bounded to the test's <c>using</c> scope (the
    /// returned <see cref="IDisposable"/> restores the previous value
    /// on <see cref="IDisposable.Dispose"/>), so a test failure cannot
    /// leak the override into a subsequent test.
    /// </remarks>
    /// <param name="keyVaultUri">Absolute Key Vault URI to use in
    /// place of any configuration-provided <c>KeyVault:Uri</c>.
    /// Whitespace / blank disables the override (and falls back to
    /// configuration), matching the production validation rule.</param>
    /// <returns>Disposable scope guard — dispose to restore the
    /// previously-active URI override (typically <see langword="null"/>).</returns>
    public static IDisposable OverrideKeyVaultUri(string keyVaultUri)
    {
        ArgumentNullException.ThrowIfNull(keyVaultUri);
        var previous = KeyVaultUriOverrideValue.Value;
        KeyVaultUriOverrideValue.Value = keyVaultUri;
        return new RestoreKeyVaultUriScope(previous);
    }

    /// <summary>
    /// Wires Azure Key Vault into <paramref name="configurationBuilder"/>
    /// when <paramref name="configuration"/> resolves
    /// <c>KeyVault:Uri</c> to an absolute URI; otherwise no-ops so
    /// local-dev hosts (which never set <c>KeyVault:Uri</c>) keep
    /// running on User-Secrets-and-env-vars only. The reload
    /// interval is read from <c>Telegram:SecretRefreshIntervalMinutes</c>
    /// (default 5) per architecture.md §10 line 1018 and §11 line
    /// 1091 so vault rotation propagates without a process restart.
    /// </summary>
    /// <param name="configurationBuilder">The
    /// <see cref="IConfigurationBuilder"/> the Key Vault provider
    /// is appended to (typically
    /// <c>builder.Configuration</c>).</param>
    /// <param name="configuration">The configuration source used to
    /// read <c>KeyVault:Uri</c> and
    /// <c>Telegram:SecretRefreshIntervalMinutes</c>; same instance
    /// as <paramref name="configurationBuilder"/> when called from
    /// Program.cs.</param>
    /// <returns><see langword="true"/> when Key Vault was wired in;
    /// <see langword="false"/> when <c>KeyVault:Uri</c> was blank
    /// or invalid.</returns>
    public static bool TryAddTelegramKeyVault(
        IConfigurationBuilder configurationBuilder,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        ArgumentNullException.ThrowIfNull(configuration);

        // AsyncLocal override takes precedence so the integration
        // suite's WebApplicationFactory<Program> harness can drive
        // this code path WITHOUT relying on the
        // ConfigureAppConfiguration callback queue (where the test's
        // in-memory `KeyVault:Uri` lands AFTER Program.cs's
        // bootstrap callback has already returned false). When the
        // override is unset (production), the read falls back to
        // IConfiguration the usual way so appsettings / env vars /
        // command-line / User Secrets all still work.
        var keyVaultUri = KeyVaultUriOverrideValue.Value;
        if (string.IsNullOrWhiteSpace(keyVaultUri))
        {
            keyVaultUri = configuration["KeyVault:Uri"];
        }

        if (string.IsNullOrWhiteSpace(keyVaultUri)
            || !Uri.TryCreate(keyVaultUri, UriKind.Absolute, out var keyVaultUriParsed))
        {
            return false;
        }

        var refreshMinutes = configuration.GetValue<int?>(
            "Telegram:SecretRefreshIntervalMinutes") ?? 5;
        if (refreshMinutes <= 0)
        {
            refreshMinutes = 5;
        }

        var credential = new DefaultAzureCredential();
        var secretClient = SecretClientFactory(keyVaultUriParsed, credential);

        configurationBuilder.AddAzureKeyVault(
            secretClient,
            new AzureKeyVaultConfigurationOptions
            {
                Manager = new TelegramKeyVaultSecretManager(),
                ReloadInterval = TimeSpan.FromMinutes(refreshMinutes),
            });

        return true;
    }

    private static SecretClient DefaultFactory(Uri vaultUri, TokenCredential credential)
        => new(vaultUri, credential);

    private sealed class RestoreFactoryScope : IDisposable
    {
        private readonly Func<Uri, TokenCredential, SecretClient>? previous;
        private bool disposed;

        public RestoreFactoryScope(Func<Uri, TokenCredential, SecretClient>? previous)
        {
            this.previous = previous;
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            SecretClientFactoryOverride.Value = this.previous;
        }
    }

    private sealed class RestoreKeyVaultUriScope : IDisposable
    {
        private readonly string? previous;
        private bool disposed;

        public RestoreKeyVaultUriScope(string? previous)
        {
            this.previous = previous;
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            KeyVaultUriOverrideValue.Value = this.previous;
        }
    }
}
