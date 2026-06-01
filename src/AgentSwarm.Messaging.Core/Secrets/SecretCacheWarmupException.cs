// -----------------------------------------------------------------------
// <copyright file="SecretCacheWarmupException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

/// <summary>
/// Thrown by <see cref="SecretCacheWarmupHostedService.StartAsync"/>
/// when one or more <see cref="SecretRefRequirement.Required"/>
/// references could not be resolved at host start-up. Carries the
/// list of failed references plus their underlying exceptions so
/// triage can see all failures at once instead of fixing them
/// one-by-one across restarts.
/// </summary>
/// <remarks>
/// Stage 3.3 iter-3 evaluator item 2: architecture.md §7.3 mandates
/// "secrets are loaded into memory at connector startup". A required
/// secret that cannot be resolved at warmup means the connector
/// CANNOT serve its first request -- failing closed at startup is
/// the only way to honour the contract.
/// </remarks>
public sealed class SecretCacheWarmupException : Exception
{
    /// <summary>Initializes a new instance with the supplied failure list.</summary>
    public SecretCacheWarmupException(IReadOnlyList<SecretCacheWarmupFailure> failures)
        : base(BuildMessage(failures))
    {
        this.Failures = new ReadOnlyCollection<SecretCacheWarmupFailure>(
            new List<SecretCacheWarmupFailure>(failures ?? Array.Empty<SecretCacheWarmupFailure>()));
    }

    /// <summary>
    /// Read-only list of every required reference that failed to
    /// resolve at startup, in source-yield order.
    /// </summary>
    public IReadOnlyList<SecretCacheWarmupFailure> Failures { get; }

    private static string BuildMessage(IReadOnlyList<SecretCacheWarmupFailure>? failures)
    {
        int count = failures?.Count ?? 0;
        if (count == 0)
        {
            return "Secret cache warmup failed (no failure details supplied).";
        }

        System.Text.StringBuilder sb = new();
        sb.Append("Secret cache warmup failed for ")
          .Append(count)
          .Append(" required secret reference(s): ");

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append('\'').Append(failures![i].SecretRef).Append('\'');
        }

        sb.Append(". See InnerException / Failures for details.");
        return sb.ToString();
    }
}

/// <summary>
/// One failed required-secret warmup attempt: the reference and the
/// exception raised by the secret provider.
/// </summary>
public sealed class SecretCacheWarmupFailure
{
    /// <summary>Initializes a new instance.</summary>
    public SecretCacheWarmupFailure(string secretRef, Exception cause)
    {
        this.SecretRef = secretRef ?? throw new ArgumentNullException(nameof(secretRef));
        this.Cause = cause ?? throw new ArgumentNullException(nameof(cause));
    }

    /// <summary>The unresolved required secret reference.</summary>
    public string SecretRef { get; }

    /// <summary>The exception raised by the secret provider at warmup.</summary>
    public Exception Cause { get; }
}
