// -----------------------------------------------------------------------
// <copyright file="CompositeSecretProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Cache slots hold <c>Lazy&lt;Task&lt;CacheEntry&gt;&gt;</c> rather than
/// the raw <see cref="CacheEntry"/> so concurrent callers de-duplicate
/// onto a single in-flight backend round-trip. Without this, N parallel
/// readers arriving at TTL expiry would each observe the stale entry,
/// each call the backend, and each race to overwrite the cache slot —
/// turning every refresh into a thundering herd against Key Vault (PR
/// #66 review thread on this file, line 126). The <see cref="Lazy{T}"/>
/// is installed atomically via
/// <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, TValue)"/>
/// (cold miss) or
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryUpdate(TKey, TValue, TValue)"/>
/// (TTL refresh): only the thread that wins the install triggers the
/// inner call; everyone else awaits the same <see cref="Task{TResult}"/>.
/// </para>
/// <para>
/// Cached secret values are tagged with
/// <see cref="LogPropertyIgnoreAttribute"/> and the holding
/// <see cref="CacheEntry"/> overrides <see cref="object.ToString"/> via
/// <see cref="SecretScrubber.Scrub"/> so a careless
/// <c>logger.LogInformation("entry={Entry}", entry)</c> emits the
/// scrubbed placeholder instead of the raw secret material — closing
/// the FR-022 / architecture.md §7.3 "never logged" requirement at the
/// cache boundary.
/// </para>
/// </remarks>
public sealed class CompositeSecretProvider : ISecretProvider
{
    private readonly ISecretProvider inner;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan refreshInterval;
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> cache;

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
        this.cache = new ConcurrentDictionary<string, Lazy<Task<CacheEntry>>>(StringComparer.Ordinal);
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
        this.cache = new ConcurrentDictionary<string, Lazy<Task<CacheEntry>>>(StringComparer.Ordinal);
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
    /// <para>
    /// Concurrent callers that arrive while a refresh is in flight (or
    /// arrive simultaneously on a cold miss) share the SAME inner
    /// round-trip via the per-key <see cref="Lazy{T}"/>: this closes
    /// the cache-stampede window that an unguarded
    /// check-then-set pattern would open at TTL expiry under load.
    /// The inner call is dispatched with <see cref="CancellationToken.None"/>
    /// so a single caller cancelling does NOT abort the refresh for
    /// the other waiters; each caller honours their own
    /// <paramref name="ct"/> through <see cref="TaskAsyncExtensions.WaitAsync{TResult}(Task{TResult}, CancellationToken)"/>.
    /// </para>
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

        // Bounded by the number of times a stampeded refresh slot can be
        // re-installed between iterations; in practice this resolves in
        // one or two passes even under heavy contention.
        while (true)
        {
            Lazy<Task<CacheEntry>> selected;

            if (this.cache.TryGetValue(secretRef, out Lazy<Task<CacheEntry>>? existing))
            {
                // Fast path: a completed, still-fresh entry serves
                // without touching the inner backend or the Lazy state
                // machine.
                if (existing.IsValueCreated)
                {
                    Task<CacheEntry> existingTask = existing.Value;

                    if (existingTask.IsCompletedSuccessfully)
                    {
                        CacheEntry cached = existingTask.Result;
                        if ((this.timeProvider.GetUtcNow() - cached.ResolvedAt) < this.refreshInterval)
                        {
                            return cached.Value;
                        }

                        // TTL has elapsed: try to atomically swap in a
                        // fresh Lazy. TryUpdate only succeeds for the
                        // single thread whose comparison value is still
                        // the slot's current Lazy reference; everyone
                        // else loops and joins the winner's task.
                        Lazy<Task<CacheEntry>> refresh = this.CreateLazy(secretRef);
                        if (this.cache.TryUpdate(secretRef, refresh, existing))
                        {
                            selected = refresh;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else if (existingTask.IsFaulted || existingTask.IsCanceled)
                    {
                        // A previous attempt left a faulted Lazy behind
                        // (e.g., the originating caller's catch handler
                        // could not evict because the dictionary slot
                        // had already been replaced and then this Lazy
                        // was re-installed by yet another thread). Try
                        // to evict and install a fresh attempt.
                        Lazy<Task<CacheEntry>> refresh = this.CreateLazy(secretRef);
                        if (this.cache.TryUpdate(secretRef, refresh, existing))
                        {
                            selected = refresh;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // Inner call is in flight: join it instead of
                        // issuing a parallel backend round-trip.
                        selected = existing;
                    }
                }
                else
                {
                    // The Lazy exists but its factory has not been
                    // invoked yet (vanishingly small window between
                    // GetOrAdd and the winning thread's .Value access).
                    // Joining it triggers the factory under the
                    // ExecutionAndPublication safety mode, so we still
                    // de-duplicate.
                    selected = existing;
                }
            }
            else
            {
                // Cold miss: try to install our Lazy. If a competing
                // thread beat us, GetOrAdd returns theirs and we join.
                Lazy<Task<CacheEntry>> ours = this.CreateLazy(secretRef);
                selected = this.cache.GetOrAdd(secretRef, ours);
            }

            return await this.AwaitAndHandleAsync(secretRef, selected, ct).ConfigureAwait(false);
        }
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

    private Lazy<Task<CacheEntry>> CreateLazy(string secretRef)
    {
        // ExecutionAndPublication guarantees the factory runs ONCE no
        // matter how many threads race onto .Value, so even if two
        // refresh attempts both succeed at TryUpdate against different
        // prior snapshots, each individual Lazy still issues at most
        // one inner call.
        return new Lazy<Task<CacheEntry>>(
            () => this.LoadAsync(secretRef),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private async Task<CacheEntry> LoadAsync(string secretRef)
    {
        // Capture the timestamp BEFORE the network call so the cache
        // freshness window starts at the moment the refresh was kicked
        // off, matching the pre-stampede-fix behaviour.
        DateTimeOffset startedAt = this.timeProvider.GetUtcNow();

        // Detach from any individual caller's CancellationToken: this
        // task is shared across every awaiter that joined the same
        // Lazy, so cancelling it would cancel the refresh for ALL of
        // them. Per-caller cancellation is honoured separately in
        // AwaitAndHandleAsync via Task.WaitAsync(ct).
        string value = await this.inner.GetSecretAsync(secretRef, CancellationToken.None).ConfigureAwait(false);
        return new CacheEntry(value, startedAt);
    }

    private async Task<string> AwaitAndHandleAsync(
        string secretRef,
        Lazy<Task<CacheEntry>> lazy,
        CancellationToken ct)
    {
        try
        {
            CacheEntry entry = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
            return entry.Value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled their wait. The shared inner task
            // may still complete (it runs with CancellationToken.None)
            // and serve concurrent / subsequent waiters, so we leave
            // the Lazy in place.
            throw;
        }
        catch
        {
            // The shared inner call faulted (SecretNotFoundException,
            // transport failure, etc.). Evict the Lazy — but only if
            // it is still the one occupying the slot — so the next
            // caller retries against the backend instead of replaying
            // the cached failure. This preserves the
            // "failures must NOT poison the cache" contract enforced
            // by CompositeSecretProviderCachingTests.
            this.cache.TryRemove(
                new KeyValuePair<string, Lazy<Task<CacheEntry>>>(secretRef, lazy));
            throw;
        }
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
