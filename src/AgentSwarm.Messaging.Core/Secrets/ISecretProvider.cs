// -----------------------------------------------------------------------
// <copyright file="ISecretProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Resolves opaque secret reference URIs (for example
/// <c>env://SLACK_SIGNING_SECRET</c> or <c>keyvault://slack-signing-secret</c>)
/// into their plain-text secret value at runtime.
/// </summary>
/// <remarks>
/// <para>
/// Introduced by Stage 3.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c> so
/// the <c>SlackSignatureValidator</c> middleware can resolve the per-workspace
/// signing secret without taking a hard dependency on a specific secret
/// backend. Stage 3.3 layers an <c>EnvironmentSecretProvider</c>, an
/// <c>AzureKeyVaultSecretProvider</c>, and a caching <c>CompositeSecretProvider</c>
/// on top of the same interface; consumers see only the contract defined here.
/// </para>
/// <para>
/// Implementations MUST:
/// </para>
/// <list type="bullet">
///   <item><description>Throw <see cref="SecretNotFoundException"/> when the
///   supplied <paramref name="secretRef"/> cannot be resolved.</description></item>
///   <item><description>Never log the resolved secret value
///   (FR-022 / architecture.md §7.3).</description></item>
///   <item><description>Honour the supplied <paramref name="ct"/> for
///   long-running network lookups.</description></item>
/// </list>
/// </remarks>
public interface ISecretProvider
{
    /// <summary>
    /// Resolves the secret value associated with <paramref name="secretRef"/>.
    /// </summary>
    /// <param name="secretRef">
    /// Provider-agnostic secret reference URI. The exact scheme set is
    /// defined by the concrete implementation; the in-memory stub used
    /// for testing accepts arbitrary strings as keys.
    /// </param>
    /// <param name="ct">Cancellation token honoured by network-backed providers.</param>
    /// <returns>
    /// The plain-text secret value. Implementations MUST NOT return
    /// <see langword="null"/>; missing secrets surface as
    /// <see cref="SecretNotFoundException"/>.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="secretRef"/> is <see langword="null"/>, empty, or
    /// whitespace.
    /// </exception>
    /// <exception cref="SecretNotFoundException">
    /// The reference is well-formed but does not resolve to any secret in
    /// the underlying store.
    /// </exception>
    Task<string> GetSecretAsync(string secretRef, CancellationToken ct);
}
