// -----------------------------------------------------------------------
// <copyright file="SecretRefDescriptor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;

/// <summary>
/// A single secret reference annotated with whether the host MUST be
/// able to resolve it at start-up
/// (<see cref="SecretRefRequirement.Required"/>) or whether the host
/// may degrade gracefully when the reference is missing
/// (<see cref="SecretRefRequirement.Optional"/>).
/// </summary>
/// <remarks>
/// Stage 3.3 iter-3 evaluator item 2: the previous iter's warmup hosted
/// service swallowed every <see cref="SecretNotFoundException"/> at
/// start-up, which weakened the architecture.md §7.3
/// "loaded into memory at connector startup" contract. The typed
/// descriptor lets a source distinguish "this secret is critical for
/// every request" (signing secret, bot token) from "this secret is
/// only used by an opt-in feature" (Socket Mode's app-level token).
/// </remarks>
public readonly record struct SecretRefDescriptor
{
    /// <summary>Initializes a new descriptor.</summary>
    public SecretRefDescriptor(string secretRef, SecretRefRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new ArgumentException(
                "Secret reference must be a non-empty, non-whitespace string.",
                nameof(secretRef));
        }

        this.SecretRef = secretRef;
        this.Requirement = requirement;
    }

    /// <summary>The secret reference URI (e.g., <c>env://SLACK_BOT_TOKEN</c>).</summary>
    public string SecretRef { get; }

    /// <summary>Whether the host MUST be able to resolve the reference at start-up.</summary>
    public SecretRefRequirement Requirement { get; }

    /// <summary>
    /// Helper factory for a required reference. Required references
    /// that fail to resolve at start-up cause
    /// <see cref="SecretCacheWarmupHostedService.StartAsync"/> to throw
    /// and the host to fail closed.
    /// </summary>
    public static SecretRefDescriptor Required(string secretRef)
        => new(secretRef, SecretRefRequirement.Required);

    /// <summary>
    /// Helper factory for an optional reference. Optional references
    /// that fail to resolve at start-up are logged at warning and
    /// skipped; the host continues to boot.
    /// </summary>
    public static SecretRefDescriptor Optional(string secretRef)
        => new(secretRef, SecretRefRequirement.Optional);
}

/// <summary>
/// Distinguishes warmup-required from warmup-optional secret
/// references. Required references that fail to resolve cause the
/// host to fail closed; optional references log a warning and are
/// retried on the per-request code path.
/// </summary>
public enum SecretRefRequirement
{
    /// <summary>
    /// The reference MUST resolve at warmup. A failure raises
    /// <see cref="SecretCacheWarmupException"/> and the host does not
    /// start.
    /// </summary>
    Required = 0,

    /// <summary>
    /// The reference is allowed to fail at warmup; the failure is
    /// logged at warning and the host continues to boot. The
    /// per-request code path will surface the failure when the
    /// reference is actually requested.
    /// </summary>
    Optional = 1,
}
