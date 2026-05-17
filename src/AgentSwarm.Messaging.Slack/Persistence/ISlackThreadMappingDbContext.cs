// -----------------------------------------------------------------------
// <copyright file="ISlackThreadMappingDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Narrow projection of <see cref="DbContext"/> exposing the
/// <c>slack_thread_mapping</c> table so the Stage 5.3
/// <see cref="Pipeline.EntityFrameworkSlackThreadMappingLookup{TContext}"/>
/// can resolve the <see cref="SlackThreadMapping.CorrelationId"/> for
/// an interactive payload without taking a direct reference on the
/// concrete <see cref="SlackPersistenceDbContext"/> type.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ISlackInboundRequestRecordDbContext"/> /
/// <see cref="ISlackAuditEntryDbContext"/> -- a single
/// <see cref="DbSet{TEntity}"/> accessor. The Stage 2.2 schema already
/// declares the unique <c>(TeamId, ChannelId, ThreadTs)</c> index that
/// the lookup query exploits.
/// </remarks>
public interface ISlackThreadMappingDbContext
{
    /// <summary>EF Core entity set for the
    /// <c>slack_thread_mapping</c> table.</summary>
    DbSet<SlackThreadMapping> SlackThreadMappings { get; }
}
