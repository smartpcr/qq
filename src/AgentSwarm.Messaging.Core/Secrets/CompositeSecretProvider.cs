// -----------------------------------------------------------------------
// <copyright file="CompositeSecretProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

/// <summary>
/// Routing <see cref="ISecretProvider"/> that selects an underlying
/// backend at construction time based on
/// <see cref="SecretProviderOptions.ProviderType"/> and then layers a
/// per-reference TTL cache on top so repeated lookups for the same
/// <c>secretRef</c> never re-hit the backend within the configured
/// refresh interval.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// architecture.md §7.3 mandates that "secrets are loaded into memory
/// at connector startup and refreshed on a configurable interval
/// (default 1 hour)". The interval is read from
/// <see cref="SecretProviderOptions.RefreshIntervalMinutes"/>; setting
/// it to <c>0</c> disables the cache entirely and forces a pass-through
/// to the backend on every call (used by tests that need to observe
/// every individual backend invocation).
/// </para>
/// <para>
/// The composite is a thin selector rather than a chain-of-responsibility
/// because production wiring only needs ONE backend per host: the
/// signing-secret reference scheme is fixed for the deployment. Tests
/// that need to combine backends do so by injecting the concrete
/// provider directly.
/// </para>
/// <para>
/// Cached secret values are tagged with
/// <see cref="LogPropertyIgnoreAttribute"/> and the holding
/// <see cref="CacheEntry"/> overrides <see cref="object.ToString"/> via
/// <see cref="SecretScrubber.Scrub"/> so a careless
/// <c>logger.LogInformation("entry={Entry}", entry)</c> emits the
/// scrubbed placeholder instead of the raw secret material -- closing
/// the FR-022 / architecture.md §7.3 "never logged" requirement at the
/// cache boundary.
/// </para>
/// </remarks>
public sealed class CompositeSecretProvider : ISecretProvider
{
    private readonly ISecretProvider inner;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan refreshInterval;
    private readonly ConcurrentDictionary<string, CacheEntry> cache;

    /// <summary>
    /// DI-friendly constructor that selects a backend based on
    /// <see cref="SecretProviderOptions.ProviderType"/>. When the
    /// configured backend is <see cref="SecretProviderType.KeyVault"/>
    /// or <see cref="SecretProviderType.Kubernetes"/> the corresponding
    /// optional provider parameter MUST have been registered (typically
    /// via
    /// <see cref="SecretProviderServiceCollectionExtensions.AddKeyVaultSecretProvider"/>
    /// or
    /// <see cref="SecretProviderServiceCollectionExtensions.AddKubernetesSecretProvider"/>);
    /// otherwise an <see cref="InvalidOperationException"/> is thrown
    /// at construction time so the misconfiguration cannot mask a
    /// missing production secret backend.
    /// </summary>
    public CompositeSecretProvider(
        IOptions<SecretProviderOptions> options,
        EnvironmentSecretProvider environmentProvider,
        InMemorySecretProvider inMemoryProvider)
        : this(options, environmentProvider, inMemoryProvider, kubernetesProvider: null, keyVaultProvider: null, TimeProvider.System)
    {
    }

    /// <summary>
    /// DI-friendly constructor with an explicit <see cref="TimeProvider"/>
    /// for tests that need to advance virtual time. Production callers
    /// use the parameterless overload above which falls back to
    /// <see cref="TimeProvider.System"/>.
    /// </summary>
    public CompositeSecretProvider(
        IOptions<SecretProviderOptions> options,
        EnvironmentSecretProvider environmentProvider,
        InMemorySecretProvider inMemoryProvider,
        TimeProvider timeProvider)
        : this(options, environmentProvider, inMemoryProvider, kubernetesProvider: null, keyVaultProvider: null, timeProvider)
    {
    }

    /// <summary>
    /// Full DI constructor that accepts the optional
    /// <see cref="KubernetesSecretProvider"/> and
    /// <see cref="KeyVaultSecretProvider"/> backends. The legacy
    /// parameters remain for tests that need to inject a specific
    /// backend; in production
    /// <see cref="SecretProviderServiceCollectionExtensions.AddSecretProvider"/>
    /// always supplies both so the iter-3 self-sufficient
    /// configuration-driven selection contract holds.
    /// </summary>
    public CompositeSecretProvider(
        IOptions<SecretProviderOptions> options,
        EnvironmentSecretProvider environmentProvider,
        InMemorySecretProvider inMemoryProvider,
        KubernetesSecretProvider? kubernetesProvider,
        KeyVaultSecretProvider? keyVaultProvider,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environmentProvider);
        ArgumentNullException.ThrowIfNull(inMemoryProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);

        SecretProviderOptions resolved = options.Value ?? new SecretProviderOptions();
        this.inner = Select(resolved, environmentProvider, inMemoryProvider, kubernetesProvider, keyVaultProvider);
        this.timeProvider = timeProvider;
        this.refreshInterval = ResolveRefreshInterval(resolved.RefreshIntervalMinutes);
        this.cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Test-only constructor that wraps an arbitrary inner provider so
    /// caching behaviour can be exercised without standing up the
    /// real backend chain. Visible to
    /// <c>AgentSwarm.Messaging.Slack.Tests</c> via
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    internal CompositeSecretProvider(
        ISecretProvider inner,
        TimeSpan refreshInterval,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.inner = inner;
        this.timeProvider = timeProvider;
        this.refreshInterval = refreshInterval < TimeSpan.Zero ? TimeSpan.Zero : refreshInterval;
        this.cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Effective refresh interval, materialized from
    /// <see cref="SecretProviderOptions.RefreshIntervalMinutes"/>.
    /// Exposed for tests so the wiring assertion does not need to
    /// duplicate the minutes-to-TimeSpan conversion.
    /// </summary>
    internal TimeSpan RefreshInterval => this.refreshInterval;

    /// <inheritdoc />
    /// <remarks>
    /// On a cache hit within <see cref="RefreshInterval"/> the inner
    /// backend is NOT consulted: the stored value is returned directly.
    /// On a miss (or when the cache entry has aged past the interval)
    /// the inner backend is queried; the result is then cached against
    /// the supplied <paramref name="secretRef"/> using
    /// <see cref="StringComparer.Ordinal"/> for the key. Failures
    /// (<see cref="SecretNotFoundException"/> and friends) are NOT
    /// cached so a transient outage does not poison the cache.
    /// </remarks>
    public async Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new ArgumentException(
                "Secret reference must be a non-empty, non-whitespace string.",
                nameof(secretRef));
        }

        if (this.refreshInterval <= TimeSpan.Zero)
        {
            // Cache disabled: every call is a direct pass-through.
            return await this.inner.GetSecretAsync(secretRef, ct).ConfigureAwait(false);
        }

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        if (this.cache.TryGetValue(secretRef, out CacheEntry? cached)
            && (now - cached.ResolvedAt) < this.refreshInterval)
        {
            return cached.Value;
        }

        string value = await this.inner.GetSecretAsync(secretRef, ct).ConfigureAwait(false);
        this.cache[secretRef] = new CacheEntry(value, now);
        return value;
    }

    /// <summary>
    /// Removes every cached entry, forcing the next
    /// <see cref="GetSecretAsync"/> call for any reference to re-hit
    /// the backend. Exposed so an operator-driven secret rotation
    /// (e.g., a Stage 4.x admin hook) can invalidate the cache without
    /// waiting for the TTL.
    /// </summary>
    public void InvalidateCache()
    {
        this.cache.Clear();
    }

    private static TimeSpan ResolveRefreshInterval(int minutes)
    {
        return minutes <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(minutes);
    }

    private static ISecretProvider Select(
        SecretProviderOptions resolved,
        EnvironmentSecretProvider environmentProvider,
        InMemorySecretProvider inMemoryProvider,
        KubernetesSecretProvider? kubernetesProvider,
        KeyVaultSecretProvider? keyVaultProvider)
    {
        return resolved.ProviderType switch
        {
            SecretProviderType.InMemory => inMemoryProvider,
            SecretProviderType.Environment => environmentProvider,
            SecretProviderType.KeyVault => keyVaultProvider
                ?? throw BuildUnregisteredException(resolved.ProviderType, "KeyVaultSecretProvider"),
            SecretProviderType.Kubernetes => kubernetesProvider
                ?? throw BuildUnregisteredException(resolved.ProviderType, "KubernetesSecretProvider"),
            _ => throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"SecretProvider:ProviderType={resolved.ProviderType} is not a recognised {nameof(SecretProviderType)} value.")),
        };
    }

    private static InvalidOperationException BuildUnregisteredException(
        SecretProviderType providerType,
        string providerTypeName)
    {
        // Reachable only when a caller invokes the test-friendly
        // public constructors with a null backend. The production
        // DI path (SecretProviderServiceCollectionExtensions.AddSecretProvider)
        // auto-registers KubernetesSecretProvider and
        // KeyVaultSecretProvider so the IConfiguration-only call IS
        // sufficient to enable ProviderType=KeyVault / Kubernetes,
        // and this branch is unreachable from configuration alone.
        string message = FormattableString.Invariant(
            $"SecretProvider:ProviderType={providerType} requires a non-null {providerTypeName} ")
            + "to be passed to CompositeSecretProvider. Use AddSecretProvider(IConfiguration) "
            + "which auto-registers all backends, or supply the backend explicitly via the test-only constructor overload.";
        return new InvalidOperationException(message);
    }

    /// <summary>
    /// Single per-reference cache slot. The resolved
    /// <see cref="Value"/> is tagged <see cref="LogPropertyIgnoreAttribute"/>
    /// and <see cref="ToString"/> delegates to
    /// <see cref="LogPropertyRedactor.RedactToString"/> so the
    /// attribute itself drives the scrub: a future author adding a new
    /// secret-bearing property only has to tag it, and the
    /// "never logged" guarantee survives the change without anyone
    /// having to update a hand-written formatter.
    /// </summary>
    internal sealed class CacheEntry
    {
        public CacheEntry(string value, DateTimeOffset resolvedAt)
        {
            this.Value = value;
            this.ResolvedAt = resolvedAt;
        }

        [LogPropertyIgnore]
        public string Value { get; }

        public DateTimeOffset ResolvedAt { get; }

        public override string ToString() => LogPropertyRedactor.RedactToString(this);
    }
}
