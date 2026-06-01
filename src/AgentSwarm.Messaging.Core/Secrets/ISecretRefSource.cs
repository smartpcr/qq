// -----------------------------------------------------------------------
// <copyright file="ISecretRefSource.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System.Collections.Generic;
using System.Threading;

/// <summary>
/// Enumerates the set of secret references that an integration
/// expects to resolve at runtime. Used by
/// <see cref="SecretCacheWarmupHostedService"/> to pre-load secrets
/// into the <see cref="CompositeSecretProvider"/> cache at connector
/// startup, satisfying architecture.md §7.3
/// ("secrets are loaded into memory at connector startup").
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Multiple sources may be registered via
/// <c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;ISecretRefSource, MyRefSource&gt;())</c>;
/// the warmup service iterates each one and resolves every yielded
/// reference.
/// </para>
/// <para>
/// Stage 3.3 iter-3 evaluator item 2 introduced
/// <see cref="SecretRefDescriptor"/> so each source can flag the
/// reference as <see cref="SecretRefRequirement.Required"/> (host
/// fails closed at startup if it doesn't resolve) or
/// <see cref="SecretRefRequirement.Optional"/> (warning is logged
/// and warmup continues; the per-request code path still raises
/// <see cref="SecretNotFoundException"/> if the reference is later
/// requested). The architecture.md §7.3 contract is satisfied for
/// every reference the source labels Required.
/// </para>
/// </remarks>
public interface ISecretRefSource
{
    /// <summary>
    /// Yields every secret reference the source knows about, each
    /// annotated with whether the host MUST be able to resolve it at
    /// startup.
    /// </summary>
    /// <param name="ct">Cancellation token honoured by network-backed sources.</param>
    IAsyncEnumerable<SecretRefDescriptor> GetSecretRefsAsync(CancellationToken ct);
}
