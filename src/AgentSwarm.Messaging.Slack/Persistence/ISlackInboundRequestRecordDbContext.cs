// -----------------------------------------------------------------------
// <copyright file="ISlackInboundRequestRecordDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Narrow projection of <see cref="DbContext"/> that the Stage 4.1
/// modal fast-path's durable idempotency store
/// (<see cref="Transport.EntityFrameworkSlackFastPathIdempotencyStore{TContext}"/>)
/// requires. The upstream <c>SlackPersistenceDbContext</c> implements
/// this interface so the Slack project can target the
/// <c>slack_inbound_request_record</c> table without taking a direct
/// reference on the context type itself.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="ISlackAuditEntryDbContext"/> -- a single
/// <see cref="DbSet{TEntity}"/> accessor plus a save-changes hook --
/// so the registration story is symmetric: the Worker host wires the
/// EF backend via
/// <see cref="Transport.SlackInboundDurabilityServiceCollectionExtensions.AddSlackFastPathDurableIdempotency{TContext}"/>.
/// </para>
/// </remarks>
public interface ISlackInboundRequestRecordDbContext
{
    /// <summary>EF Core entity set for the
    /// <c>slack_inbound_request_record</c> table.</summary>
    DbSet<SlackInboundRequestRecord> SlackInboundRequestRecords { get; }

    /// <summary>
    /// Persists pending changes. Same semantics as
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
