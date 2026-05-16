// -----------------------------------------------------------------------
// <copyright file="ISlackAuditEntryDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Narrow projection of <see cref="DbContext"/> that the Stage 3.1
/// <see cref="EntityFrameworkSlackAuditEntryWriter{TContext}"/> requires:
/// access to the <see cref="SlackAuditEntry"/> table plus a save-changes
/// hook. The upstream <c>MessagingDbContext</c> implements this
/// interface so the Slack project can target the audit table without
/// taking a reference on the context itself (which would invert the
/// existing dependency direction Slack -> Persistence).
/// </summary>
public interface ISlackAuditEntryDbContext
{
    /// <summary>EF Core entity set for the <c>slack_audit_entry</c> table.</summary>
    DbSet<SlackAuditEntry> SlackAuditEntries { get; }

    /// <summary>
    /// Persists pending changes. Same semantics as
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
