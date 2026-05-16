// -----------------------------------------------------------------------
// <copyright file="RetentionTestDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Test-only EF Core context that implements EVERY Slack
/// <c>DbContext</c> projection used by Stage 7.1:
/// <see cref="ISlackAuditEntryDbContext"/> and
/// <see cref="ISlackInboundRequestRecordDbContext"/>. Used by
/// <see cref="SlackAuditLoggerTests"/> and
/// <see cref="SlackRetentionCleanupServiceTests"/> so the generic
/// constraint <c>where TContext : class, ISlackAuditEntryDbContext,
/// ISlackInboundRequestRecordDbContext</c> on
/// <see cref="SlackRetentionCleanupService{TContext}"/> is satisfied
/// without binding to <see cref="SlackPersistenceDbContext"/>.
/// </summary>
public sealed class RetentionTestDbContext
    : DbContext, ISlackAuditEntryDbContext, ISlackInboundRequestRecordDbContext
{
    /// <summary>Creates the context with the supplied options.</summary>
    public RetentionTestDbContext(DbContextOptions<RetentionTestDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    public DbSet<SlackAuditEntry> SlackAuditEntries => this.Set<SlackAuditEntry>();

    /// <inheritdoc />
    public DbSet<SlackInboundRequestRecord> SlackInboundRequestRecords => this.Set<SlackInboundRequestRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddSlackEntities();
    }
}
