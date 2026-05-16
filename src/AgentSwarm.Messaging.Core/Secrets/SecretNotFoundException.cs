// -----------------------------------------------------------------------
// <copyright file="SecretNotFoundException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;

/// <summary>
/// Thrown by an <see cref="ISecretProvider"/> when a secret reference
/// is syntactically valid but does not resolve to a stored secret.
/// </summary>
/// <remarks>
/// The exception preserves <see cref="SecretRef"/> so callers (most
/// notably <c>SlackSignatureValidator</c>) can log the missing reference
/// for triage without ever touching the resolved value.
/// </remarks>
public sealed class SecretNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance with the supplied <paramref name="secretRef"/>.
    /// The message includes the reference so structured loggers capture it
    /// even when the caller forgets to log <see cref="SecretRef"/> directly.
    /// </summary>
    /// <param name="secretRef">The reference that failed to resolve.</param>
    public SecretNotFoundException(string secretRef)
        : base(BuildMessage(secretRef))
    {
        this.SecretRef = secretRef ?? string.Empty;
    }

    /// <summary>
    /// Initializes a new instance with the supplied
    /// <paramref name="secretRef"/> and inner exception describing the
    /// underlying failure (for example a network error from a remote vault).
    /// </summary>
    public SecretNotFoundException(string secretRef, Exception innerException)
        : base(BuildMessage(secretRef), innerException)
    {
        this.SecretRef = secretRef ?? string.Empty;
    }

    /// <summary>
    /// The reference URI that did not resolve. Always non-null; defaults
    /// to <see cref="string.Empty"/> when the caller passed <c>null</c>.
    /// </summary>
    public string SecretRef { get; }

    private static string BuildMessage(string? secretRef)
    {
        return string.IsNullOrEmpty(secretRef)
            ? "Secret reference is missing or empty."
            : $"Secret reference '{secretRef}' did not resolve to any stored secret.";
    }
}
