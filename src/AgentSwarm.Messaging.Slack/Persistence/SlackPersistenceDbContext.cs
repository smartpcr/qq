// -----------------------------------------------------------------------
// <copyright file="SlackPersistenceDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Concrete EF Core <see cref="DbContext"/> for the Slack connector's
/// persistent surface. Implements <see cref="ISlackAuditEntryDbContext"/>
/// so it can back <see cref="EntityFrameworkSlackAuditEntryWriter{TContext}"/>
/// in the production Worker host, replacing the in-memory writer that
/// only kept rejection audit rows for the lifetime of the process.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The Worker host registers this context via
/// <see cref="SlackAuditPersistenceServiceCollectionExtensions.AddSlackAuditPersistence{TContext}"/>
/// with a configured provider (SQLite by default, SQL Server in
/// production composition roots) so the EF writer wins the
/// <c>TryAddSingleton&lt;ISlackAuditEntryWriter, ...&gt;</c> registration
/// added by <c>AddSlackSignatureValidation</c>.
/// </para>
/// <para>
/// The context owns ONLY the Slack connector's tables (audit log,
/// workspace config, thread mappings, inbound request idempotency
/// ledger). It is deliberately separate from the shared
/// <c>MessagingDbContext</c> that future stages will introduce in the
/// <c>AgentSwarm.Messaging.Persistence</c> project: that context will
/// either inherit from this one or include the same
/// <see cref="SlackModelBuilderExtensions.AddSlackEntities"/> call.
/// </para>
/// </remarks>
public class SlackPersistenceDbContext : DbContext, ISlackAuditEntryDbContext, ISlackWorkspaceConfigDbContext
{
    /// <summary>
    /// Creates the context with the supplied options. The options must
    /// have a provider configured (e.g. <c>UseSqlite</c> or
    /// <c>UseSqlServer</c>) by the composition root.
    /// </summary>
    public SlackPersistenceDbContext(DbContextOptions<SlackPersistenceDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Protected constructor for derived contexts (e.g. the future
    /// <c>MessagingDbContext</c> that will own the connector tables for
    /// the whole gateway).
    /// </summary>
    protected SlackPersistenceDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <inheritdoc />
    public DbSet<SlackAuditEntry> SlackAuditEntries => this.Set<SlackAuditEntry>();

    /// <inheritdoc />
    public DbSet<SlackWorkspaceConfig> SlackWorkspaceConfigs => this.Set<SlackWorkspaceConfig>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Single source of truth for Slack entity mappings -- reuses the
        // same hook the test harness uses so the production schema can
        // never drift from the schema asserted by the Stage 2.3 tests.
        modelBuilder.AddSlackEntities();
    }
}
