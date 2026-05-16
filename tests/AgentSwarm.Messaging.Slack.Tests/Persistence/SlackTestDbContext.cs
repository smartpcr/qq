// -----------------------------------------------------------------------
// <copyright file="SlackTestDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Test-only <see cref="DbContext"/> that registers all four Slack entity
/// configurations from the Slack project so the schema can be validated in
/// isolation (without depending on the upstream <c>MessagingDbContext</c>
/// in the Persistence project).
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Production code uses the upstream <c>MessagingDbContext</c>; this
/// subclass exists only to give the Slack test assembly a self-contained
/// schema to exercise against SQLite (in-memory or file-backed).
/// </para>
/// </remarks>
public sealed class SlackTestDbContext : DbContext, ISlackInboundRequestRecordDbContext
{
    /// <summary>
    /// Initialises a new <see cref="SlackTestDbContext"/> with the
    /// supplied options. The standard pattern is to construct with
    /// <see cref="DbContextOptionsBuilder{TContext}.UseSqlite(string)"/>.
    /// </summary>
    /// <param name="options">DbContext options provided by the caller.</param>
    public SlackTestDbContext(DbContextOptions<SlackTestDbContext> options)
        : base(options)
    {
    }

    /// <summary>Workspaces.</summary>
    public DbSet<SlackWorkspaceConfig> Workspaces => Set<SlackWorkspaceConfig>();

    /// <summary>Thread-to-task mappings.</summary>
    public DbSet<SlackThreadMapping> ThreadMappings => Set<SlackThreadMapping>();

    /// <summary>
    /// Inbound idempotency ledger. This is the single public accessor for
    /// <see cref="SlackInboundRequestRecord"/> on this test context;
    /// <see cref="ISlackInboundRequestRecordDbContext.SlackInboundRequestRecords"/>
    /// is implemented explicitly below and delegates here, so there is
    /// exactly one source of truth for the underlying
    /// <see cref="DbSet{TEntity}"/>.
    /// </summary>
    public DbSet<SlackInboundRequestRecord> InboundRequests
        => Set<SlackInboundRequestRecord>();

    /// <summary>Audit entries.</summary>
    public DbSet<SlackAuditEntry> AuditEntries => Set<SlackAuditEntry>();

    /// <inheritdoc />
    /// <remarks>
    /// Implemented explicitly so the test context still exposes only ONE
    /// public <see cref="DbSet{TEntity}"/> accessor for
    /// <see cref="SlackInboundRequestRecord"/> -- the long-standing
    /// <see cref="InboundRequests"/> property used by the test fixtures.
    /// Code that depends on the interface (notably the generic
    /// <c>EntityFrameworkSlackFastPathIdempotencyStore&lt;TContext&gt;</c>
    /// store, which constrains <c>TContext : ISlackInboundRequestRecordDbContext</c>)
    /// reaches the same underlying EF set via the interface contract,
    /// while in-assembly test code keeps using the descriptive
    /// <see cref="InboundRequests"/> name. This avoids the "two public
    /// accessors for the same entity" anti-pattern flagged in PR review.
    /// </remarks>
    DbSet<SlackInboundRequestRecord> ISlackInboundRequestRecordDbContext.SlackInboundRequestRecords
        => this.InboundRequests;

    /// <inheritdoc />
    /// <remarks>
    /// Stage 2.3: delegates to
    /// <see cref="SlackModelBuilderExtensions.AddSlackEntities(ModelBuilder)"/>
    /// so the test context exercises the same auto-discovery hook
    /// (<see cref="ModelBuilderExtensions.ApplyConfigurationsFromAssembly(ModelBuilder, System.Reflection.Assembly, System.Func{System.Type, bool}?)"/>)
    /// the upstream <c>MessagingDbContext</c> will invoke in production.
    /// New <c>IEntityTypeConfiguration</c> implementations added to the
    /// Slack assembly are registered automatically.
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.AddSlackEntities();
    }
}
