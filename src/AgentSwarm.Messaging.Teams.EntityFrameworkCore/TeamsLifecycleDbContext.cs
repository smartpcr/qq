using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core <see cref="DbContext"/> exposing the <c>AgentQuestions</c> and <c>CardStates</c>
/// tables that back the SQL implementations of
/// <see cref="AgentSwarm.Messaging.Abstractions.IAgentQuestionStore"/> and
/// <see cref="AgentSwarm.Messaging.Teams.ICardStateStore"/>. Aligned with
/// <c>implementation-plan.md</c> §3.3 steps 1 and 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate DbContext from <see cref="TeamsConversationReferenceDbContext"/></b>:
/// the conversation-reference table has an existing EF Core migration. Adding the
/// question / card-state tables to the same context would invalidate that migration
/// snapshot and force a coupled redeploy of the conversation-reference table. Keeping
/// these tables in a dedicated context lets operators add the Stage 3.3 tables in
/// isolation — and lets the in-memory test fixture call <c>EnsureCreated()</c> on a
/// minimal schema for unit tests.
/// </para>
/// <para>
/// <b>Filtered indexes</b>: per §3.3 step 2 the AgentQuestions table carries two
/// filtered indexes:
/// </para>
/// <list type="bullet">
/// <item><description><c>(ConversationId, Status) WHERE Status = 'Open'</c> for
/// <c>GetOpenByConversationAsync</c> — keeps the bare <c>approve</c>/<c>reject</c>
/// disambiguation lookup a sub-millisecond index seek.</description></item>
/// <item><description><c>(Status, ExpiresAt) WHERE Status = 'Open'</c> for
/// <c>GetOpenExpiredAsync</c> — narrows the periodic
/// <see cref="AgentSwarm.Messaging.Teams.Lifecycle.QuestionExpiryProcessor"/> scan to the
/// active working set so it does not regress as the audit table grows.</description></item>
/// </list>
/// <para>
/// <b>Filtered-index syntax / provider portability</b>: the <c>HasFilter</c> calls below
/// emit ANSI quoted-identifier syntax (<c>"Status" = 'Open'</c>) which SQL Server parses
/// correctly as <c>[Status] = 'Open'</c> in the generated <c>CREATE INDEX</c> statement.
/// Production targets SQL Server, so the filter <i>is</i> enforced there.
/// </para>
/// <para>
/// <b>SQLite caveat (tests only)</b>: SQLite has a long-standing compatibility quirk
/// where double-quoted strings inside an expression context — including the WHERE clause
/// of <c>CREATE INDEX … WHERE …</c> — may be interpreted as string literals rather than
/// identifiers when the surrounding context allows it (see SQLite's
/// <c>"double-quoted string literals"</c> behaviour). In that case the filter expression
/// degenerates to <c>'Status' = 'Open'</c>, which is a constant <c>false</c>, and SQLite
/// either rejects the partial index or silently builds one that matches no rows. Either
/// way the in-memory <see cref="Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions">SQLite</see>
/// test suite does <b>not</b> validate filtered-index <i>selectivity</i> — it only
/// validates that the migration applies cleanly. Filtered-index <i>behaviour</i>
/// (predicate restriction, index hit rate) is exercised solely against SQL Server in
/// the integration suite. Future maintainers should not assume the SQLite-backed unit
/// tests cover the <c>WHERE Status = 'Open'</c> predicate; if you change the filter,
/// also add or update a SQL-Server-targeted integration test.
/// </para>
/// </remarks>
public class TeamsLifecycleDbContext : DbContext
{
    /// <summary>Construct the context with the supplied options (provider, connection string).</summary>
    /// <param name="options">EF Core options bound by DI.</param>
    public TeamsLifecycleDbContext(DbContextOptions<TeamsLifecycleDbContext> options)
        : base(options)
    {
    }

    /// <summary>Backing <c>DbSet</c> for the <c>AgentQuestions</c> table.</summary>
    public DbSet<AgentQuestionEntity> AgentQuestions => Set<AgentQuestionEntity>();

    /// <summary>Backing <c>DbSet</c> for the <c>CardStates</c> table.</summary>
    public DbSet<CardStateEntity> CardStates => Set<CardStateEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AgentQuestionEntity>(ConfigureAgentQuestion);
        modelBuilder.Entity<CardStateEntity>(ConfigureCardState);
    }

    /// <summary>
    /// Apply provider-agnostic <see cref="DateTimeOffset"/> ticks conversion when the
    /// active provider is SQLite. SQLite cannot ORDER BY <c>DateTimeOffset</c> columns
    /// natively because the values are stored as ISO-8601 strings; converting to a
    /// 64-bit ticks long preserves total ordering across UTC offsets while staying
    /// transparent to callers that consume <see cref="DateTimeOffset"/> through the
    /// store contract. SQL Server's native <c>datetimeoffset</c> type already orders
    /// correctly, so production deployments keep the canonical storage format.
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

    private static void ConfigureAgentQuestion(EntityTypeBuilder<AgentQuestionEntity> builder)
    {
        builder.ToTable("AgentQuestions");
        builder.HasKey(e => e.QuestionId);

        builder.Property(e => e.QuestionId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.AgentId).HasMaxLength(128).IsRequired();
        builder.Property(e => e.TaskId).HasMaxLength(128);
        builder.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.TargetUserId).HasMaxLength(128);
        builder.Property(e => e.TargetChannelId).HasMaxLength(256);
        builder.Property(e => e.ConversationId).HasMaxLength(256);
        builder.Property(e => e.Title).HasMaxLength(512).IsRequired();
        builder.Property(e => e.Body).IsRequired();
        builder.Property(e => e.Severity).HasMaxLength(32).IsRequired();
        builder.Property(e => e.AllowedActionsJson).IsRequired();
        builder.Property(e => e.ExpiresAt).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(16).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.ResolvedAt);

        // Open-by-conversation lookup (Stage 3.2 bare approve/reject disambiguation).
        // NOTE: HasFilter emits ANSI quoted-identifier syntax. SQL Server enforces this
        // predicate; SQLite (used only by the in-memory unit tests) may treat
        // "Status" as a string literal due to its legacy DQS quirk, in which case the
        // partial index is effectively un-validated by the test suite. See the class
        // remarks for the full caveat.
        builder.HasIndex(e => new { e.ConversationId, e.Status })
            .HasDatabaseName("IX_AgentQuestions_ConversationId_Status_Open")
            .HasFilter("\"Status\" = 'Open'");

        // Expiry scan (Stage 3.3 QuestionExpiryProcessor).
        // Same SQLite-vs-SQL-Server caveat as the index above — predicate selectivity
        // is exercised against SQL Server only.
        builder.HasIndex(e => new { e.Status, e.ExpiresAt })
            .HasDatabaseName("IX_AgentQuestions_Status_ExpiresAt_Open")
            .HasFilter("\"Status\" = 'Open'");
    }

    private static void ConfigureCardState(EntityTypeBuilder<CardStateEntity> builder)
    {
        builder.ToTable("CardStates");
        builder.HasKey(e => e.QuestionId);

        builder.Property(e => e.QuestionId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.ActivityId).HasMaxLength(256).IsRequired();
        builder.Property(e => e.ConversationId).HasMaxLength(256).IsRequired();
        builder.Property(e => e.ConversationReferenceJson).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(16).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
    }
}
