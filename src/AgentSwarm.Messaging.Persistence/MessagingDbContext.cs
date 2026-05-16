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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessagingDbContext).Assembly);
    }
}
