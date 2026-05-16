using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core <see cref="DbContext"/> exposing the <c>OutboxMessages</c> table that backs
/// <see cref="SqlMessageOutbox"/>. Kept in its own context (sibling to
/// <see cref="TeamsLifecycleDbContext"/> and <see cref="TeamsConversationReferenceDbContext"/>)
/// so the outbox migration can be applied independently of the question / card-state
/// schema.
/// </summary>
public class TeamsOutboxDbContext : DbContext
{
    /// <summary>Construct with the supplied options.</summary>
    public TeamsOutboxDbContext(DbContextOptions<TeamsOutboxDbContext> options)
        : base(options)
    {
    }

    /// <summary>Backing <c>DbSet</c> for the <c>OutboxMessages</c> table.</summary>
    public DbSet<OutboxEntryEntity> OutboxEntries => Set<OutboxEntryEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<OutboxEntryEntity>(ConfigureOutboxEntry);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        base.ConfigureConventions(configurationBuilder);

        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            // SQLite cannot ORDER BY DateTimeOffset natively — convert to UTC ticks so
            // the lease-expiry / next-retry comparisons in DequeueAsync work correctly.
            configurationBuilder
                .Properties<DateTimeOffset>()
                .HaveConversion<DateTimeOffsetToTicksConverter>();

            configurationBuilder
                .Properties<DateTimeOffset?>()
                .HaveConversion<NullableDateTimeOffsetToTicksConverter>();
        }
    }

    private sealed class DateTimeOffsetToTicksConverter : ValueConverter<DateTimeOffset, long>
    {
        public DateTimeOffsetToTicksConverter()
            : base(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero))
        {
        }
    }

    private sealed class NullableDateTimeOffsetToTicksConverter : ValueConverter<DateTimeOffset?, long?>
    {
        public NullableDateTimeOffsetToTicksConverter()
            : base(
                v => v.HasValue ? v.Value.UtcTicks : (long?)null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : (DateTimeOffset?)null)
        {
        }
    }

    private static void ConfigureOutboxEntry(EntityTypeBuilder<OutboxEntryEntity> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(e => e.OutboxEntryId);

        builder.Property(e => e.OutboxEntryId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(e => e.Destination).HasMaxLength(512).IsRequired();
        builder.Property(e => e.DestinationType).HasMaxLength(32);
        builder.Property(e => e.DestinationId).HasMaxLength(256);
        builder.Property(e => e.PayloadType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.PayloadJson).IsRequired();
        builder.Property(e => e.ConversationReferenceJson);
        builder.Property(e => e.ActivityId).HasMaxLength(256);
        builder.Property(e => e.ConversationId).HasMaxLength(256);
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(2048);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.NextRetryAt);
        builder.Property(e => e.DeliveredAt);
        builder.Property(e => e.LeaseExpiresAt);
        builder.Property(e => e.RetryCount).IsRequired();

        // Hot-path index for the outbox poll query — selects rows whose Status is
        // Pending (eligible for first attempt or scheduled retry) or whose Processing
        // lease has expired (crash recovery). Sorted by Status then NextRetryAt for the
        // FIFO-by-readiness ordering the dispatcher expects.
        builder.HasIndex(e => new { e.Status, e.NextRetryAt })
            .HasDatabaseName("IX_OutboxMessages_Status_NextRetryAt");

        // Index supporting lease-recovery sweep — finds Processing rows whose lease has
        // expired so DequeueAsync can reclaim them.
        builder.HasIndex(e => new { e.Status, e.LeaseExpiresAt })
            .HasDatabaseName("IX_OutboxMessages_Status_LeaseExpiresAt");
    }
}
