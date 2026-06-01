// -----------------------------------------------------------------------
// <copyright file="SlackAuditQuery.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;

/// <summary>
/// Filter specification for
/// <see cref="ISlackAuditLogger.QueryAsync(SlackAuditQuery, System.Threading.CancellationToken)"/>.
/// Mirrors the architecture.md §4.6 specification: every field is
/// optional and unset fields are ignored. Set fields are combined with
/// AND semantics so triage queries can narrow to a single correlation
/// id, restrict by team/channel/user, or pivot on direction/outcome
/// inside a bounded time window.
/// </summary>
/// <remarks>
/// <para>
/// Modeled as a sealed reference type (rather than a struct) so future
/// stages can add filter fields without breaking ABI for existing
/// callers. Operator-supplied filter strings are compared with
/// <see cref="StringComparison.Ordinal"/> at the EF translation
/// boundary -- mirror the column casing exactly when querying.
/// </para>
/// </remarks>
public sealed class SlackAuditQuery
{
    /// <summary>
    /// End-to-end correlation identifier (architecture.md §3.5). When
    /// set, only rows whose <see cref="Entities.SlackAuditEntry.CorrelationId"/>
    /// equals this value are returned.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Task identifier filter. <c>null</c> rows are excluded when this
    /// value is set (LINQ-to-Entities translates <c>x == "T"</c> with
    /// null-propagation semantics that already match SQL three-valued
    /// logic).
    /// </summary>
    public string? TaskId { get; set; }

    /// <summary>Agent identifier filter.</summary>
    public string? AgentId { get; set; }

    /// <summary>Slack workspace (team) identifier filter.</summary>
    public string? TeamId { get; set; }

    /// <summary>Slack channel identifier filter.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Slack user identifier filter.</summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Direction filter -- typically <c>inbound</c> or <c>outbound</c>
    /// per <see cref="Entities.SlackAuditEntry.Direction"/>'s contract.
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>
    /// Outcome filter -- one of the canonical
    /// <see cref="Entities.SlackAuditEntry.Outcome"/> markers
    /// (<c>success</c>, <c>rejected_auth</c>, <c>rejected_signature</c>,
    /// <c>duplicate</c>, <c>error</c>, ...). Plain string for
    /// portability across stores and stages that may add new outcomes.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>
    /// Inclusive lower bound on
    /// <see cref="Entities.SlackAuditEntry.Timestamp"/>. <c>null</c>
    /// means "no lower bound".
    /// </summary>
    public DateTimeOffset? FromTimestamp { get; set; }

    /// <summary>
    /// Inclusive upper bound on
    /// <see cref="Entities.SlackAuditEntry.Timestamp"/>. <c>null</c>
    /// means "no upper bound".
    /// </summary>
    public DateTimeOffset? ToTimestamp { get; set; }

    /// <summary>
    /// Optional row cap. <c>null</c> applies no limit; integration
    /// queries that must bound the result set pass a small positive
    /// integer. Negative or zero values are treated as "no limit"
    /// (callers expecting an empty result should test the filter
    /// instead).
    /// </summary>
    public int? Limit { get; set; }
}
