using AgentSwarm.Messaging.Abstractions;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessagingDbContext).Assembly);
    }
}
