// -----------------------------------------------------------------------
// <copyright file="NullAuditFallbackSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Default no-op <see cref="IAuditFallbackSink"/>. Registered via
/// <c>TryAddSingleton</c> by the in-memory bootstrap so the
/// <c>TelegramUpdatePipeline</c> can always resolve the dependency
/// without forcing every test harness to wire a real implementation.
/// <c>ServiceCollectionExtensions.AddMessagingPersistence</c> (in
/// <c>AgentSwarm.Messaging.Persistence</c>) calls
/// <c>services.Replace</c> with <c>FileAuditFallbackSink</c> so
/// production hosts get the durable backstop the Stage 5.3 brief
/// requires.
/// </summary>
/// <remarks>
/// Tests that depend on the fallback behaviour MUST register an
/// explicit double (<see cref="IAuditFallbackSink"/>) rather than
/// rely on this implementation. Silently dropping the entry is
/// acceptable ONLY for dev / unit-test bootstraps that also use the
/// <c>NullAuditLogger</c> primary — pairing
/// <c>PersistentAuditLogger</c> with <see cref="NullAuditFallbackSink"/>
/// would silently violate the "log every inbound command" Stage 5.3
/// contract on any audit-DB outage, which is why
/// <c>AddMessagingPersistence</c> always replaces this binding.
/// </remarks>
public sealed class NullAuditFallbackSink : IAuditFallbackSink
{
    /// <inheritdoc />
    public Task EnqueueAsync(AuditEntry entry, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task EnqueueAsync(HumanResponseAuditEntry entry, CancellationToken ct) => Task.CompletedTask;
}
