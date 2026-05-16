// -----------------------------------------------------------------------
// <copyright file="ISlackWorkspaceConfigDbContext.cs" company="Microsoft Corp.">
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
/// <see cref="EntityFrameworkSlackWorkspaceConfigStore{TContext}"/>
/// requires: read/write access to the <c>slack_workspace_config</c> table
/// plus a save-changes hook. Keeping this contract separate from
/// <see cref="ISlackAuditEntryDbContext"/> means a future upstream
/// <c>MessagingDbContext</c> can implement just the surface it needs and
/// the Slack project does not have to take a hard reference on the
/// concrete context type.
/// </summary>
public interface ISlackWorkspaceConfigDbContext
{
    /// <summary>EF Core entity set for the <c>slack_workspace_config</c> table.</summary>
    DbSet<SlackWorkspaceConfig> SlackWorkspaceConfigs { get; }

    /// <summary>
    /// Persists pending changes. Same semantics as
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
