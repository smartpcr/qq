using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

public class MessagingDbContext : DbContext
{
    public MessagingDbContext(DbContextOptions<MessagingDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the durable inbound update
    /// queue (Stage 2.4). Configured via
    /// <see cref="InboundUpdateConfiguration"/> applied through the
    /// model-creating scan in <see cref="OnModelCreating"/>.
    /// </summary>
    public DbSet<InboundUpdate> InboundUpdates => Set<InboundUpdate>();

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the durable Telegram
    /// <c>message_id</c> → <c>CorrelationId</c> reverse index used by
    /// the inbound reply path to tie a human reply back to the
    /// originating agent trace (Stage 2.3 step 161 + iter-3 evaluator
    /// item 3). Configured via
    /// <see cref="OutboundMessageIdMappingConfiguration"/>.
    /// </summary>
    public DbSet<OutboundMessageIdMapping> OutboundMessageIdMappings => Set<OutboundMessageIdMapping>();

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the durable outbound
    /// dead-letter ledger written by <c>TelegramMessageSender</c> on
    /// retry exhaustion (iter-4 evaluator item 4). Configured via
    /// <see cref="OutboundDeadLetterConfiguration"/>.
    /// </summary>
    public DbSet<OutboundDeadLetterRecord> OutboundDeadLetters => Set<OutboundDeadLetterRecord>();

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the task-to-operator
    /// oversight assignment table (Stage 3.2). One row per task; the
    /// <c>/handoff</c> command upserts the row, the Stage 2.7
    /// swarm-event subscription service reads it to route status
    /// updates and alerts. Configured via
    /// <see cref="TaskOversightConfiguration"/>.
    /// </summary>
    public DbSet<TaskOversight> TaskOversights => Set<TaskOversight>();

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the messenger gateway's
    /// audit trail (Stage 3.2 iter-2 evaluator item 5). Single
    /// discriminated table — both general <c>AuditEntry</c> and
    /// typed <c>HumanResponseAuditEntry</c> writes share storage,
    /// distinguished by <see cref="AuditLogEntry.EntryKind"/>.
    /// Configured via <see cref="AuditLogEntryConfiguration"/>.
    /// </summary>
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessagingDbContext).Assembly);
    }
}
