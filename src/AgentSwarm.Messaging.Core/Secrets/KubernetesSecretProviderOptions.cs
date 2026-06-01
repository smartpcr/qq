// -----------------------------------------------------------------------
// <copyright file="KubernetesSecretProviderOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

/// <summary>
/// Strongly-typed options for the <see cref="KubernetesSecretProvider"/>.
/// Bound from the
/// <see cref="SectionName"/> (<c>SecretProvider:Kubernetes</c>) section
/// of <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// </summary>
/// <remarks>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (architecture.md §7.3). Kubernetes mounts secrets as individual files
/// under a configurable directory; the provider reads
/// <c>{MountPath}/{name}</c> when asked to resolve <c>k8s://{name}</c>.
/// </remarks>
public sealed class KubernetesSecretProviderOptions
{
    /// <summary>
    /// Configuration section name (<c>SecretProvider:Kubernetes</c>)
    /// the options are bound from.
    /// </summary>
    public const string SectionName = "SecretProvider:Kubernetes";

    /// <summary>
    /// Default mount path. Matches the convention recommended by
    /// architecture.md §7.3: agent-swarm secrets live under a
    /// dedicated subdirectory (<c>agentswarm</c>) so the volume mount
    /// is not shared with unrelated workload secrets.
    /// </summary>
    public const string DefaultMountPath = "/var/run/secrets/agentswarm";

    /// <summary>
    /// Directory under which Kubernetes mounts each secret as a file
    /// whose name matches the part of the reference after <c>k8s://</c>.
    /// Defaults to <see cref="DefaultMountPath"/>.
    /// </summary>
    public string MountPath { get; set; } = DefaultMountPath;

    /// <summary>
    /// When <see langword="true"/> (the default), trailing
    /// <c>\r</c> / <c>\n</c> bytes are stripped from the file content
    /// before returning the secret. Kubernetes appends a trailing
    /// newline to most secret values; without this trim an HMAC
    /// computed over the resolved value would silently disagree with
    /// one computed by callers that read the secret directly.
    /// </summary>
    public bool TrimTrailingNewline { get; set; } = true;
}
