// -----------------------------------------------------------------------
// <copyright file="ISlackAuditLogger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Stage 7.1 broad audit-logger contract introduced by architecture.md
/// §2.14 / §4.6. Provides the canonical write seam
/// (<see cref="LogAsync"/>) used by every inbound and outbound Slack
/// processing path, together with the
/// <see cref="QueryAsync(SlackAuditQuery, CancellationToken)"/> read
/// seam that backs operator triage queries against
/// <c>slack_audit_entry</c>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The interface is intentionally a superset of
/// <see cref="ISlackAuditEntryWriter"/>: the Slack project ships a
/// single production implementation (<see cref="SlackAuditLogger{TContext}"/>)
/// that implements both contracts so existing call sites that already
/// depend on <see cref="ISlackAuditEntryWriter.AppendAsync"/> (signature
/// validation, authorization, idempotency, command dispatch, interaction
/// handling, outbound dispatch, modal open, thread manager,
/// DirectApiClient) automatically route through
/// <see cref="LogAsync"/>. Direct consumers may depend on this
/// interface when they need the query side as well.
/// </para>
/// <para>
/// Production implementations persist via EF Core through the shared
/// <c>MessagingDbContext</c> (or any context that implements
/// <see cref="ISlackAuditEntryDbContext"/>). Tests bind an in-memory
/// stub.
/// </para>
/// </remarks>
public interface ISlackAuditLogger
{
    /// <summary>
    /// Persists <paramref name="entry"/> as an immutable audit row.
    /// Implementations MUST be safe for concurrent invocation from
    /// independent request pipelines and MUST NOT mutate
    /// <paramref name="entry"/>. Audit-write failures are wrapped at
    /// the call site (every existing pipeline catches and logs)
    /// rather than propagated so an audit-pipeline blip never
    /// breaks the inbound HTTP response or outbound dispatch.
    /// </summary>
    /// <param name="entry">Populated audit entry. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LogAsync(SlackAuditEntry entry, CancellationToken ct);

    /// <summary>
    /// Returns every <see cref="SlackAuditEntry"/> that matches the
    /// supplied <paramref name="query"/> filters, ordered by
    /// <see cref="SlackAuditEntry.Timestamp"/> ascending so triage
    /// queries see oldest-first by default. Filters are combined with
    /// AND semantics; unset filters are ignored.
    /// </summary>
    /// <param name="query">Filter spec. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SlackAuditEntry>> QueryAsync(
        SlackAuditQuery query,
        CancellationToken ct);
}
