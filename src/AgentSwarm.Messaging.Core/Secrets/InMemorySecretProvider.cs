// -----------------------------------------------------------------------
// <copyright file="InMemorySecretProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Thread-safe in-memory <see cref="ISecretProvider"/> for unit tests,
/// integration tests, and local development environments. Stores a
/// dictionary of <c>secretRef -&gt; secret value</c> seeded at construction
/// time and mutable via <see cref="Set(string, string)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Introduced by Stage 3.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c> as
/// the "stub for testing" called out in the brief. Production deployments
/// register the secret provider added by Stage 3.3 instead.
/// </para>
/// <para>
/// The provider performs an <em>ordinal</em> dictionary lookup. Whitespace
/// is not stripped, so the reference passed in must match exactly. Missing
/// references surface as <see cref="SecretNotFoundException"/>, which is
/// what the signature validator relies on for its rejection path.
/// </para>
/// </remarks>
public sealed class InMemorySecretProvider : ISecretProvider
{
    private readonly ConcurrentDictionary<string, string> store;

    /// <summary>
    /// Creates an empty provider.
    /// </summary>
    public InMemorySecretProvider()
        : this(seed: null)
    {
    }

    /// <summary>
    /// Creates a provider seeded with the supplied initial secret map.
    /// </summary>
    /// <param name="seed">
    /// Optional initial map of <c>secretRef -&gt; value</c>. <c>null</c> or
    /// empty creates an empty store. The reference comparer is
    /// <see cref="StringComparer.Ordinal"/> so lookups behave the same on
    /// every platform.
    /// </param>
    public InMemorySecretProvider(IReadOnlyDictionary<string, string>? seed)
    {
        this.store = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        if (seed is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> entry in seed)
        {
            this.Set(entry.Key, entry.Value);
        }
    }

    /// <summary>
    /// Adds or replaces the stored value for <paramref name="secretRef"/>.
    /// </summary>
    /// <param name="secretRef">Non-empty, non-whitespace reference key.</param>
    /// <param name="value">Plain-text secret value; <c>null</c> is rejected.</param>
    public void Set(string secretRef, string value)
    {
        ValidateRef(secretRef);
        ArgumentNullException.ThrowIfNull(value);
        this.store[secretRef] = value;
    }

    /// <summary>
    /// Removes the entry for <paramref name="secretRef"/>, if present.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if a value was removed; <see langword="false"/>
    /// when no entry matched.
    /// </returns>
    public bool Remove(string secretRef)
    {
        ValidateRef(secretRef);
        return this.store.TryRemove(secretRef, out _);
    }

    /// <inheritdoc />
    public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
    {
        ValidateRef(secretRef);

        ct.ThrowIfCancellationRequested();

        if (this.store.TryGetValue(secretRef, out string? value))
        {
            return Task.FromResult(value);
        }

        throw new SecretNotFoundException(secretRef);
    }

    private static void ValidateRef(string secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new ArgumentException(
                "Secret reference must be a non-empty, non-whitespace string.",
                nameof(secretRef));
        }
    }
}
