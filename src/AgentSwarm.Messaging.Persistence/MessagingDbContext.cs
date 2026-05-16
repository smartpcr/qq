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
    /// <see cref="DbSet{TEntity}"/> backing the durable outbox
    /// (Stage 4.1). One row per outbound Telegram message: the
    /// <c>OutboundQueueProcessor</c> drains the queue in
    /// severity-priority order and the connector's
    /// <c>SendMessageAsync</c> / <c>SendQuestionAsync</c> path
    /// enqueues into it. Configured via
    /// <see cref="OutboundMessageConfiguration"/>.
    /// </summary>
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();

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

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the operator identity
    /// mapping table (Stage 3.4). One row per
    /// <c>(TelegramUserId, TelegramChatId, WorkspaceId)</c> binding;
    /// queried by <see cref="PersistentOperatorRegistry"/> for runtime
    /// authorization, alias resolution, alert fallback routing, and
    /// the <c>/start</c> onboarding upsert. Configured via
    /// <see cref="OperatorBindingConfiguration"/>.
    /// </summary>
    public DbSet<OperatorBinding> OperatorBindings => Set<OperatorBinding>();

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the durable pending-question
    /// store (Stage 3.5). One row per question successfully sent to
    /// Telegram and awaiting an operator response; lifecycle tracked
    /// via <see cref="PendingQuestionRecord.Status"/>
    /// (<see cref="Abstractions.PendingQuestionStatus.Pending"/> →
    /// <see cref="Abstractions.PendingQuestionStatus.AwaitingComment"/>
    /// → <see cref="Abstractions.PendingQuestionStatus.Answered"/> /
    /// <see cref="Abstractions.PendingQuestionStatus.TimedOut"/>).
    /// Queried by <see cref="PersistentPendingQuestionStore"/> and
    /// polled by <c>QuestionTimeoutService</c> via the
    /// <c>(Status, ExpiresAt)</c> index. Configured via
    /// <see cref="PendingQuestionRecordConfiguration"/>.
    /// </summary>
    public DbSet<PendingQuestionRecord> PendingQuestions => Set<PendingQuestionRecord>();

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the sliding-window inbound
    /// deduplication ledger (Stage 4.3). One row per processed or
    /// in-flight event id; the row's
    /// <see cref="ProcessedEvent.ProcessedAt"/> column distinguishes
    /// the reservation phase (NULL) from the sticky-processed phase
    /// (non-null). Queried by <see cref="PersistentDeduplicationService"/>
    /// and purged on a fixed cadence by
    /// <see cref="DeduplicationCleanupService"/>. Configured via
    /// <see cref="ProcessedEventConfiguration"/>.
    /// </summary>
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessagingDbContext).Assembly);
    }
}
