// -----------------------------------------------------------------------
// <copyright file="KubernetesSecretProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="ISecretProvider"/> backed by the Kubernetes
/// "secret-as-file" pattern: each referenced secret is read from
/// <c>{MountPath}/{name}</c>, where <c>{name}</c> is the portion of
/// the reference URI after the <see cref="Scheme"/> prefix.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// the brief lists <c>Kubernetes</c> alongside <c>Environment</c> and
/// <c>KeyVault</c> as a configuration-driven secret backend. The
/// implementation uses only file I/O so it has no external SDK
/// dependency and works against the standard
/// <c>kubernetes.io/secret</c> volume projection.
/// </para>
/// <para>
/// Both <c>k8s://</c> and <c>kube://</c> prefixes are accepted so
/// operator-facing configuration can use either common form. The
/// extracted name is validated to be a single path segment -- any
/// reference containing a directory separator, a parent-directory
/// segment (<c>..</c>), or rooted path is rejected with
/// <see cref="ArgumentException"/> to prevent traversal outside the
/// mount.
/// </para>
/// </remarks>
public sealed class KubernetesSecretProvider : ISecretProvider
{
    /// <summary>Reference-URI scheme this provider responds to.</summary>
    public const string Scheme = "k8s://";

    /// <summary>Alternate reference-URI scheme accepted for operator convenience.</summary>
    public const string AlternateScheme = "kube://";

    private readonly KubernetesSecretProviderOptions options;
    private readonly Func<string, CancellationToken, Task<string?>> reader;

    /// <summary>
    /// DI-friendly constructor that reads from the real file system.
    /// </summary>
    public KubernetesSecretProvider(IOptions<KubernetesSecretProviderOptions> options)
        : this(options?.Value, reader: null)
    {
    }

    /// <summary>
    /// Test-only constructor that lets unit tests substitute a
    /// deterministic file reader so the mount-path traversal contract
    /// is verifiable without touching real disk.
    /// </summary>
    internal KubernetesSecretProvider(
        KubernetesSecretProviderOptions? options,
        Func<string, CancellationToken, Task<string?>>? reader)
    {
        this.options = options ?? new KubernetesSecretProviderOptions();
        this.reader = reader ?? DefaultReadFromDisk;

        if (string.IsNullOrWhiteSpace(this.options.MountPath))
        {
            throw new ArgumentException(
                $"{nameof(KubernetesSecretProviderOptions)}.{nameof(KubernetesSecretProviderOptions.MountPath)} must be a non-empty path.",
                nameof(options));
        }
    }

    /// <inheritdoc />
    public async Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new ArgumentException(
                "Secret reference must be a non-empty, non-whitespace string.",
                nameof(secretRef));
        }

        ct.ThrowIfCancellationRequested();

        string name = ExtractName(secretRef);
        ValidateName(name, secretRef);

        string fullPath = ResolveFullPath(name);
        string? raw = await this.reader(fullPath, ct).ConfigureAwait(false);
        if (raw is null || raw.Length == 0)
        {
            throw new SecretNotFoundException(secretRef);
        }

        return this.options.TrimTrailingNewline ? raw.TrimEnd('\r', '\n') : raw;
    }

    private static string ExtractName(string secretRef)
    {
        if (secretRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return secretRef[Scheme.Length..];
        }

        if (secretRef.StartsWith(AlternateScheme, StringComparison.OrdinalIgnoreCase))
        {
            return secretRef[AlternateScheme.Length..];
        }

        return secretRef;
    }

    private static void ValidateName(string name, string originalRef)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                $"Kubernetes secret reference '{originalRef}' did not yield a non-empty file name.",
                nameof(originalRef));
        }

        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0
            || name.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(name))
        {
            throw new ArgumentException(
                $"Kubernetes secret reference '{originalRef}' resolves outside the configured mount path; path traversal is rejected.",
                nameof(originalRef));
        }
    }

    private string ResolveFullPath(string name)
    {
        string candidate = Path.Combine(this.options.MountPath, name);
        string fullCandidate = Path.GetFullPath(candidate);
        string fullMount = Path.GetFullPath(this.options.MountPath);

        // Defence in depth: even though ValidateName rejects traversal,
        // we double-check the resolved absolute path is genuinely inside
        // the mount directory. Path.Combine + GetFullPath normalize any
        // platform-specific separator quirks.
        if (!fullCandidate.StartsWith(fullMount, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Resolved secret path '{fullCandidate}' is outside the configured mount '{fullMount}'.");
        }

        return fullCandidate;
    }

    private static async Task<string?> DefaultReadFromDisk(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }
}
