// -----------------------------------------------------------------------
// <copyright file="ISlackAuditEntryWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Single-row append seam to the <c>slack_audit_entry</c> table introduced
/// by Stage 2.2. Stage 3.1 routes its signature-rejection records through
/// this contract so the audit pipeline records EVERY rejection -- not
/// just the in-memory ones surfaced by the validator's diagnostic sink.
/// </summary>
/// <remarks>
/// <para>
/// The interface is deliberately narrow: callers that need batched
/// inserts or query semantics use the broader audit-logger surface
/// introduced by Stage 5. Stage 3.1 only needs append.
/// </para>
/// <para>
/// Production deployments bind
/// <see cref="EntityFrameworkSlackAuditEntryWriter{TContext}"/> against a
/// <c>MessagingDbContext</c>; tests and developer-laptop setups bind
/// <see cref="InMemorySlackAuditEntryWriter"/>. The Stage 3.1 DI
/// extension registers the in-memory writer by default and the EF writer
/// only when the host has registered a <c>DbContext</c> implementing
/// <see cref="ISlackAuditEntryDbContext"/>.
/// </para>
/// </remarks>
public interface ISlackAuditEntryWriter
{
    /// <summary>
    /// Appends <paramref name="entry"/> to the audit log.
    /// Implementations MUST be safe for concurrent invocation from
    /// independent request pipelines.
    /// </summary>
    /// <param name="entry">Populated audit entry. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendAsync(SlackAuditEntry entry, CancellationToken ct);
}
