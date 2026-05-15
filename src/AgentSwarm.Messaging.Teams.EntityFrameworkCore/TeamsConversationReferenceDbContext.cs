using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core <see cref="DbContext"/> exposing the <c>ConversationReferences</c> table that
/// backs <see cref="SqlConversationReferenceStore"/>. Provider-agnostic — wired against
/// SQL Server in production (per <c>architecture.md</c> §9.2) and SQLite in unit tests.
/// </summary>
/// <remarks>
/// All indexes declared in <see cref="OnModelCreating"/> are filtered to match the
/// implementation-plan §4.1 spec:
/// <list type="bullet">
/// <item><description>Unique <c>(AadObjectId, TenantId)</c> WHERE <c>AadObjectId IS NOT NULL</c> — user-scoped key.</description></item>
/// <item><description>Non-unique <c>(InternalUserId, TenantId)</c> WHERE <c>InternalUserId IS NOT NULL</c> — proactive-routing key.</description></item>
/// <item><description>Unique <c>(ChannelId, TenantId)</c> WHERE <c>ChannelId IS NOT NULL</c> — channel-scoped key.</description></item>
/// <item><description>Non-clustered <c>TenantId</c>.</description></item>
/// <item><description>Filtered index on <c>IsActive = true</c> for <c>GetAllActiveAsync</c> and pre-send checks.</description></item>
/// <item><description>Filtered index on <c>(ConversationId)</c> WHERE <c>IsActive = 1</c> — covers the hot-path lookup
/// performed by <c>SqlConversationReferenceStore.GetByConversationIdAsync</c>, which is called from
/// <c>TeamsMessengerConnector.SendMessageAsync</c> on every outbound proactive message. Required to keep
/// outbound P95 latency under the FR-007 3-second target at the 1000+ concurrent-user scale.</description></item>
/// </list>
/// Filtered-index syntax differs by provider (SQL Server uses <c>WHERE [Col] IS NOT NULL</c>;
/// SQLite uses <c>WHERE "Col" IS NOT NULL</c>); EF Core emits the correct dialect per
/// configured provider when calling <c>HasFilter</c> with a quoted column name.
/// </remarks>
public class TeamsConversationReferenceDbContext : DbContext
{
    /// <summary>Construct the context with the supplied options (provider, connection string).</summary>
    /// <param name="options">EF Core options bound by DI.</param>
    public TeamsConversationReferenceDbContext(DbContextOptions<TeamsConversationReferenceDbContext> options)
        : base(options)
    {
    }

    /// <summary>Backing <c>DbSet</c> for the <c>ConversationReferences</c> table.</summary>
    public DbSet<ConversationReferenceEntity> ConversationReferences => Set<ConversationReferenceEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ConversationReferenceEntity>(ConfigureConversationReference);
    }

    private static void ConfigureConversationReference(EntityTypeBuilder<ConversationReferenceEntity> builder)
    {
        builder.ToTable("ConversationReferences");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasMaxLength(64).IsRequired();
        builder.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.AadObjectId).HasMaxLength(64);
        builder.Property(e => e.InternalUserId).HasMaxLength(128);
        builder.Property(e => e.ChannelId).HasMaxLength(256);
        builder.Property(e => e.TeamId).HasMaxLength(256);
        builder.Property(e => e.ServiceUrl).HasMaxLength(512).IsRequired();
        builder.Property(e => e.ConversationId).HasMaxLength(256).IsRequired();
        builder.Property(e => e.BotId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.ConversationJson).IsRequired();
        builder.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(e => e.DeactivationReason).HasMaxLength(64);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasIndex(e => new { e.AadObjectId, e.TenantId })
            .HasDatabaseName("IX_ConversationReferences_AadObjectId_TenantId")
            .IsUnique()
            .HasFilter("\"AadObjectId\" IS NOT NULL");

        builder.HasIndex(e => new { e.InternalUserId, e.TenantId })
            .HasDatabaseName("IX_ConversationReferences_InternalUserId_TenantId")
            .HasFilter("\"InternalUserId\" IS NOT NULL");

        builder.HasIndex(e => new { e.ChannelId, e.TenantId })
            .HasDatabaseName("IX_ConversationReferences_ChannelId_TenantId")
            .IsUnique()
            .HasFilter("\"ChannelId\" IS NOT NULL");

        builder.HasIndex(e => e.TenantId)
            .HasDatabaseName("IX_ConversationReferences_TenantId");

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_ConversationReferences_IsActive")
            .HasFilter("\"IsActive\" = 1");

        // Hot-path index: SqlConversationReferenceStore.GetByConversationIdAsync filters
        // by ConversationId AND IsActive on every outbound proactive message. A filtered
        // non-unique index on ConversationId WHERE IsActive = 1 keeps the active-only
        // working set narrow and lets the lookup remain a sub-millisecond seek even at
        // the FR-007 1000+ concurrent-user / <3 s P95 outbound-latency target. The index
        // is intentionally non-unique because Bot Framework conversation IDs are not
        // strictly globally unique across tenants.
        builder.HasIndex(e => e.ConversationId)
            .HasDatabaseName("IX_ConversationReferences_ConversationId")
            .HasFilter("\"IsActive\" = 1");
    }
}
