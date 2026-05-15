using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

public class MessagingDbContext : DbContext
{
    public MessagingDbContext(DbContextOptions<MessagingDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Stage 2.3 — durable (<c>ChatId</c>, <c>TelegramMessageId</c>) →
    /// <c>CorrelationId</c> mapping written by
    /// <see cref="PersistentMessageIdTracker"/> after every successful
    /// Telegram send so reply correlation survives process restarts.
    /// </summary>
    public DbSet<OutboundMessageIdMapping> OutboundMessageIdMappings => Set<OutboundMessageIdMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessagingDbContext).Assembly);

        modelBuilder.Entity<OutboundMessageIdMapping>(b =>
        {
            // Composite primary key — Telegram message_id is only unique
            // within a chat, so the chat id must participate in the key
            // to prevent cross-chat collisions.
            b.HasKey(m => new { m.ChatId, m.TelegramMessageId });
            b.Property(m => m.CorrelationId).IsRequired().HasMaxLength(256);
            b.Property(m => m.RecordedAt).IsRequired();
            b.HasIndex(m => m.CorrelationId);
        });
    }
}

