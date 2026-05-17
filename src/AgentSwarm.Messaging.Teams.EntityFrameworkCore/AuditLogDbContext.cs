using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core <see cref="DbContext"/> exposing the append-only <c>AuditLog</c> table that
/// backs <see cref="SqlAuditLogger"/> and <see cref="SqlAuditLogQueryService"/> per
/// <c>tech-spec.md</c> §4.3 (Canonical Audit Record Schema) and
/// <c>implementation-plan.md</c> §5.2 steps 1–2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated DbContext (rather than reusing
/// <see cref="TeamsLifecycleDbContext"/> or
/// <see cref="TeamsConversationReferenceDbContext"/>)</b>: the audit table is the
/// compliance-bedrock concern of the gateway. Coupling its migration to the lifecycle
/// tables would force a coordinated redeploy of operational data every time the audit
/// schema changes (and vice versa). A separate context also matches the storage-layer
/// separation of duties — the application service principal is granted
/// <c>INSERT, SELECT</c> on <c>AuditLog</c> but is denied <c>UPDATE, DELETE</c>, so the
/// audit grant model lives independently of the lifecycle table grants.
/// </para>
/// <para>
/// <b>Index strategy</b> (see <see cref="ConfigureAuditLog"/> below): non-clustered PK
/// on <see cref="AuditLogEntity.Id"/>, clustered index on
/// <see cref="AuditLogEntity.Timestamp"/> (chronological scan is the dominant access
/// pattern for compliance review), and a non-clustered index on
/// <see cref="AuditLogEntity.CorrelationId"/> for the trace-replay query implemented
/// by <c>IAuditLogQueryService.GetByCorrelationIdAsync</c> (see
/// <see cref="SqlAuditLogQueryService"/>).
/// </para>
/// <para>
/// <b>Immutability enforcement</b>: the EF migration installs SQL Server
/// <c>INSTEAD OF UPDATE</c> and <c>INSTEAD OF DELETE</c> triggers on the table that
/// raise an error and abort the transaction. Tests using SQLite install equivalent
/// <c>BEFORE UPDATE</c> / <c>BEFORE DELETE</c> <c>RAISE(ABORT)</c> triggers in the
/// fixture so the same scenarios exercise immutability against SQLite. See
/// <c>tests/AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests/AuditLogStoreFixture.cs</c>
/// for the SQLite-flavoured trigger SQL.
/// </para>
/// <para>
/// <b>SQLite <see cref="DateTimeOffset"/> handling</b>: SQLite cannot order
/// <see cref="DateTimeOffset"/> columns natively because the values are stored as
/// ISO-8601 strings. The context applies a ticks-based value conversion when the
/// provider is SQLite so chronological ordering of the test fixture matches
/// production. SQL Server's native <c>datetimeoffset</c> type already orders
/// correctly. Same convention as <see cref="TeamsLifecycleDbContext"/>.
/// </para>
/// </remarks>
public class AuditLogDbContext : DbContext
{
    /// <summary>Construct the context with the supplied options (provider, connection string).</summary>
    /// <param name="options">EF Core options bound by DI.</param>
    public AuditLogDbContext(DbContextOptions<AuditLogDbContext> options)
        : base(options)
    {
    }

    /// <summary>Backing <c>DbSet</c> for the <c>AuditLog</c> table.</summary>
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AuditLogEntity>(ConfigureAuditLog);
    }

    /// <summary>
    /// Apply provider-agnostic <see cref="DateTimeOffset"/> ticks conversion when the
    /// active provider is SQLite. See class remarks for the rationale.
    /// </summary>
    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        base.ConfigureConventions(configurationBuilder);

        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
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

    private static void ConfigureAuditLog(EntityTypeBuilder<AuditLogEntity> builder)
    {
        builder.ToTable("AuditLog");

        // PK shape — clustering preference (non-clustered on Id so the clustered
        // index can target Timestamp) is encoded as a SQL-Server-specific annotation
        // in the migration itself rather than via IsClustered() here. Calling
        // IsClustered() on the model builder pulls in
        // Microsoft.EntityFrameworkCore.SqlServer at runtime, which the SQLite test
        // fixture cannot load (and SQLite would ignore the annotation anyway). The
        // migration's `Annotation("SqlServer:Clustered", false)` on the PK and
        // `Annotation("SqlServer:Clustered", true)` on the Timestamp index achieve
        // the same effect on production SQL Server without runtime coupling.
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedOnAdd();

        builder.Property(e => e.Timestamp).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.EventType).HasMaxLength(32).IsRequired();
        builder.Property(e => e.ActorId).HasMaxLength(128).IsRequired();
        builder.Property(e => e.ActorType).HasMaxLength(16).IsRequired();
        builder.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.AgentId).HasMaxLength(128);
        builder.Property(e => e.TaskId).HasMaxLength(128);
        builder.Property(e => e.ConversationId).HasMaxLength(256);
        builder.Property(e => e.Action).HasMaxLength(128).IsRequired();
        builder.Property(e => e.PayloadJson).IsRequired();
        builder.Property(e => e.Outcome).HasMaxLength(16).IsRequired();
        builder.Property(e => e.Checksum).HasMaxLength(64).IsRequired();

        // Index on (Timestamp, Id) — compliance review scans chronologically and
        // tech-spec.md §4.3 implies a date-range query is the bedrock access pattern.
        // The composite (Timestamp, Id) form keeps the index deterministic when many
        // entries share the same wall-clock timestamp. The migration applies the
        // SqlServer-specific clustered annotation; SQLite materializes a standard
        // non-clustered index, which is the correct behaviour for the test scenarios.
        builder.HasIndex(e => new { e.Timestamp, e.Id })
            .HasDatabaseName("IX_AuditLog_Timestamp");

        // Non-clustered index on CorrelationId for the canonical trace-replay query
        // (GetByCorrelationIdAsync).
        builder.HasIndex(e => e.CorrelationId)
            .HasDatabaseName("IX_AuditLog_CorrelationId");

        // Non-clustered index on ActorId for GetByActorAsync — actor-keyed forensic
        // review is the second canonical compliance query.
        builder.HasIndex(e => e.ActorId)
            .HasDatabaseName("IX_AuditLog_ActorId");
    }
}
