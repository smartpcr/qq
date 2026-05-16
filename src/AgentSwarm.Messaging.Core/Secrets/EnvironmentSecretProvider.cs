// -----------------------------------------------------------------------
// <copyright file="EnvironmentSecretProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// <see cref="ISecretProvider"/> backed by
/// <see cref="Environment.GetEnvironmentVariable(string)"/>. Resolves both
/// the <c>env://VAR_NAME</c> URI scheme called out in
/// <c>SlackWorkspaceConfig.SigningSecretRef</c> documentation and bare
/// environment-variable names supplied without a scheme.
/// </summary>
/// <remarks>
/// <para>
/// Implementation-plan §Stage 3.1 / §Stage 3.3. The provider is the
/// production fallback selected by
/// <see cref="SecretProviderType.Environment"/> in
/// <see cref="SecretProviderOptions"/>. It runs in-process and performs
/// no network I/O, so the supplied
/// <see cref="CancellationToken"/> is honoured by a single
/// <see cref="CancellationToken.ThrowIfCancellationRequested"/> check.
/// </para>
/// <para>
/// The provider intentionally accepts a custom resolver delegate so unit
/// tests can simulate an environment without polluting the process. The
/// default resolver delegates to
/// <see cref="Environment.GetEnvironmentVariable(string)"/>.
/// </para>
/// </remarks>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    /// <summary>
    /// Reference-URI scheme this provider responds to.
    /// <c>env://VAR_NAME</c> resolves to
    /// <c>Environment.GetEnvironmentVariable("VAR_NAME")</c>.
    /// </summary>
    public const string Scheme = "env://";

    private readonly Func<string, string?> resolver;

    /// <summary>
    /// Creates a provider that reads from the process environment.
    /// </summary>
    public EnvironmentSecretProvider()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>
    /// Creates a provider that reads through the supplied
    /// <paramref name="resolver"/> instead of the process environment.
    /// Used by tests to inject a deterministic lookup table.
    /// </summary>
    /// <param name="resolver">
    /// Function that maps a variable name to its value or
    /// <see langword="null"/> when the variable is not set.
    /// </param>
    public EnvironmentSecretProvider(Func<string, string?> resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new ArgumentException(
                "Secret reference must be a non-empty, non-whitespace string.",
                nameof(secretRef));
        }

        ct.ThrowIfCancellationRequested();

        string variableName = ExtractVariableName(secretRef);
        string? value = this.resolver(variableName);

        if (string.IsNullOrEmpty(value))
        {
            throw new SecretNotFoundException(secretRef);
        }

        return Task.FromResult(value);
    }

    private static string ExtractVariableName(string secretRef)
    {
        if (secretRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return secretRef[Scheme.Length..];
        }

        return secretRef;
    }
}
