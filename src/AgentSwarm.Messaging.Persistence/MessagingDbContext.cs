using System.Collections.ObjectModel;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core context for the messenger persistence store. Hosts the durable
/// inbox (<see cref="DiscordInteractionRecord"/>), outbound queue
/// (<see cref="OutboundMessage"/>), guild registry
/// (<see cref="GuildBinding"/>), pending-question store
/// (<see cref="PendingQuestionRecord"/>), audit log
/// (<see cref="AuditLogEntry"/>), and dead-letter store
/// (<see cref="DeadLetterMessage"/>) -- the full Stage 2.1 schema per
/// architecture.md Section 3.1 / 3.2 and implementation-plan Stage 2.1.
/// </summary>
/// <remarks>
/// <para>
/// All Discord snowflake (<c>ulong</c>) columns are stored as signed
/// <c>INTEGER</c> via <see cref="SnowflakeConverter"/> /
/// <see cref="NullableSnowflakeConverter"/>. Snowflakes are 41-bit timestamp
/// + 22 bits of worker/sequence, so they fit comfortably in 63 bits and the
/// reinterpretation cast is loss-free for every Discord-issued id.
/// </para>
/// <para>
/// Collection-shaped columns on the shared <see cref="GuildBinding"/> record
/// (<see cref="GuildBinding.AllowedRoleIds"/> and
/// <see cref="GuildBinding.CommandRestrictions"/>) are stored as JSON via
/// <see cref="JsonSerializer"/>. The accompanying <see cref="ValueComparer"/>
/// instances do structural element-wise comparison so EF change-tracking
/// behaves correctly even though the wrapped read-only collection types do
/// not implement value equality natively.
/// </para>
/// </remarks>
public class MessagingDbContext : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Designed for DI registration via <c>AddDbContext</c>.</summary>
    public MessagingDbContext(DbContextOptions<MessagingDbContext> options)
        : base(options)
    {
    }

    /// <summary>Durable inbox of Discord interactions awaiting / undergoing processing.</summary>
    public DbSet<DiscordInteractionRecord> DiscordInteractions => Set<DiscordInteractionRecord>();

    /// <summary>Guild/channel registry tying Discord bindings to swarm tenants.</summary>
    public DbSet<GuildBinding> GuildBindings => Set<GuildBinding>();

    /// <summary>Durable outbound queue feeding the platform sender.</summary>
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();

    /// <summary>Pending agent questions awaiting human response.</summary>
    public DbSet<PendingQuestionRecord> PendingQuestions => Set<PendingQuestionRecord>();

    /// <summary>Append-only audit log of operator-visible events.</summary>
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    /// <summary>Dead-letter store for outbound messages that exhausted retries.</summary>
    public DbSet<DeadLetterMessage> DeadLetterMessages => Set<DeadLetterMessage>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureDiscordInteractionRecord(modelBuilder);
        ConfigureGuildBinding(modelBuilder);
        ConfigureOutboundMessage(modelBuilder);
        ConfigurePendingQuestionRecord(modelBuilder);
        ConfigureAuditLogEntry(modelBuilder);
        ConfigureDeadLetterMessage(modelBuilder);
    }

    private static void ConfigureDiscordInteractionRecord(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DiscordInteractionRecord>(entity =>
        {
            entity.ToTable("DiscordInteractions");

            // At-most-once delivery (architecture.md §4.8 / §10.2) is gated
            // by the InteractionId PK plus an explicit UNIQUE companion index:
            // SQLite materialises the PK as a unique B-tree, and the companion
            // index is what the generated migration (and Stage 2.1 schema
            // assertion tests) expect by literal index name. Stage 2.2's
            // PersistAsync relies on the unique-collision exception to signal
            // a duplicate webhook replay -- the PK alone is enough at runtime,
            // but the named index keeps the EF model snapshot, the migration
            // output, and the schema verification test in lockstep.
            entity.HasKey(x => x.InteractionId);

            entity.HasIndex(x => x.InteractionId)
                .IsUnique()
                .HasDatabaseName("IX_DiscordInteractions_InteractionId_Unique");

            entity.Property(x => x.InteractionId)
                .HasConversion(SnowflakeConverter)
                .ValueGeneratedNever();

            entity.Property(x => x.InteractionType)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(x => x.GuildId).HasConversion(SnowflakeConverter).IsRequired();
            entity.Property(x => x.ChannelId).HasConversion(SnowflakeConverter).IsRequired();
            entity.Property(x => x.UserId).HasConversion(SnowflakeConverter).IsRequired();

            entity.Property(x => x.RawPayload).IsRequired();
            entity.Property(x => x.ReceivedAt).IsRequired();
            entity.Property(x => x.ProcessedAt);

            entity.Property(x => x.IdempotencyStatus)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(x => x.AttemptCount).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.ErrorDetail);

            entity.HasIndex(x => new { x.IdempotencyStatus, x.ReceivedAt })
                .HasDatabaseName("IX_DiscordInteractions_Status_ReceivedAt");
        });
    }

    private static void ConfigureGuildBinding(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuildBinding>(entity =>
        {
            entity.ToTable("GuildBindings");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).ValueGeneratedNever();

            entity.Property(x => x.GuildId).HasConversion(SnowflakeConverter).IsRequired();
            entity.Property(x => x.ChannelId).HasConversion(SnowflakeConverter).IsRequired();

            entity.Property(x => x.ChannelPurpose)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(x => x.TenantId).IsRequired();
            entity.Property(x => x.WorkspaceId).IsRequired();

            entity.Property(x => x.AllowedRoleIds)
                .HasConversion(AllowedRoleIdsConverter)
                .Metadata.SetValueComparer(AllowedRoleIdsComparer);

            entity.Property(x => x.CommandRestrictions)
                .HasConversion(CommandRestrictionsConverter)
                .Metadata.SetValueComparer(CommandRestrictionsComparer);

            entity.Property(x => x.RegisteredAt).IsRequired();
            entity.Property(x => x.IsActive).IsRequired();

            entity.HasIndex(x => new { x.GuildId, x.ChannelId, x.WorkspaceId })
                .IsUnique()
                .HasDatabaseName("IX_GuildBindings_Guild_Channel_Workspace_Unique");

            entity.HasIndex(x => new { x.GuildId, x.ChannelPurpose })
                .HasDatabaseName("IX_GuildBindings_Guild_Purpose");
        });
    }

    private static void ConfigureOutboundMessage(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboundMessage>(entity =>
        {
            entity.ToTable("OutboundMessages");
            entity.HasKey(x => x.MessageId);

            entity.Property(x => x.MessageId).ValueGeneratedNever();

            entity.Property(x => x.IdempotencyKey).IsRequired();
            entity.Property(x => x.ChatId).IsRequired();

            entity.Property(x => x.Severity).HasConversion<int>().IsRequired();
            entity.Property(x => x.Status).HasConversion<int>().IsRequired();
            entity.Property(x => x.SourceType).HasConversion<int>().IsRequired();

            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.SourceEnvelopeJson);
            entity.Property(x => x.SourceId);

            entity.Property(x => x.AttemptCount).IsRequired();
            entity.Property(x => x.MaxAttempts)
                .HasDefaultValue(OutboundMessage.DefaultMaxAttempts)
                .IsRequired();

            entity.Property(x => x.NextRetryAt);
            entity.Property(x => x.PlatformMessageId);

            entity.Property(x => x.CorrelationId).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.SentAt);
            entity.Property(x => x.ErrorDetail);

            entity.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName("IX_OutboundMessages_IdempotencyKey_Unique");

            entity.HasIndex(x => new { x.Status, x.Severity, x.NextRetryAt })
                .HasDatabaseName("IX_OutboundMessages_Status_Severity_NextRetryAt");
        });
    }

    private static void ConfigurePendingQuestionRecord(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingQuestionRecord>(entity =>
        {
            entity.ToTable("PendingQuestions");
            entity.HasKey(x => x.QuestionId);

            entity.Property(x => x.QuestionId).ValueGeneratedNever().IsRequired();
            entity.Property(x => x.AgentQuestion).IsRequired();

            entity.Property(x => x.DiscordChannelId).HasConversion(SnowflakeConverter).IsRequired();
            entity.Property(x => x.DiscordMessageId).HasConversion(SnowflakeConverter).IsRequired();
            entity.Property(x => x.DiscordThreadId).HasConversion(NullableSnowflakeConverter);

            entity.Property(x => x.DefaultActionId);
            entity.Property(x => x.DefaultActionValue);

            entity.Property(x => x.ExpiresAt).IsRequired();

            entity.Property(x => x.Status)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(x => x.SelectedActionId);
            entity.Property(x => x.SelectedActionValue);
            entity.Property(x => x.RespondentUserId).HasConversion(NullableSnowflakeConverter);

            entity.Property(x => x.StoredAt).IsRequired();
            entity.Property(x => x.CorrelationId).IsRequired();

            entity.HasIndex(x => new { x.Status, x.ExpiresAt })
                .HasDatabaseName("IX_PendingQuestions_Status_ExpiresAt");

            // Recovery sweep lookup per architecture.md Section 3.1.
            entity.HasIndex(x => new { x.DiscordChannelId, x.DiscordMessageId })
                .HasDatabaseName("IX_PendingQuestions_Channel_Message");
        });
    }

    private static void ConfigureAuditLogEntry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.ToTable("AuditLog");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Platform).IsRequired();
            entity.Property(x => x.ExternalUserId).IsRequired();
            entity.Property(x => x.MessageId).IsRequired();
            entity.Property(x => x.Details).IsRequired();
            entity.Property(x => x.Timestamp).IsRequired();
            entity.Property(x => x.CorrelationId).IsRequired();

            entity.HasIndex(x => x.CorrelationId)
                .HasDatabaseName("IX_AuditLog_CorrelationId");

            entity.HasIndex(x => new { x.Platform, x.Timestamp })
                .HasDatabaseName("IX_AuditLog_Platform_Timestamp");
        });
    }

    private static void ConfigureDeadLetterMessage(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeadLetterMessage>(entity =>
        {
            entity.ToTable("DeadLetterMessages");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.OriginalMessageId).IsRequired();
            entity.Property(x => x.ChatId).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.ErrorReason).IsRequired();
            entity.Property(x => x.FailedAt).IsRequired();
            entity.Property(x => x.AttemptCount).IsRequired();

            // Architecture.md Section 3.2 pins the relationship as
            // `DeadLetterMessage 1--1 OutboundMessage (via OriginalMessageId)`
            // and Stage 2.3 of the implementation-plan calls
            // OriginalMessageId a foreign key. We configure both halves of
            // that contract: a HasOne/WithOne relationship targeted at
            // OutboundMessage.MessageId (which EF backs with a real FK
            // constraint at the SQLite level) and an explicit UNIQUE index
            // on OriginalMessageId so duplicate dead-letter writes for the
            // same source collide instead of accumulating. OnDelete.Restrict
            // prevents an OutboundMessage row from being deleted while a
            // dead-letter row still points to it -- the dead-letter store is
            // an operator-triage surface that must outlive cleanup of the
            // queue table.
            entity.HasOne<OutboundMessage>()
                .WithOne()
                .HasForeignKey<DeadLetterMessage>(x => x.OriginalMessageId)
                .HasPrincipalKey<OutboundMessage>(x => x.MessageId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(x => x.OriginalMessageId)
                .IsUnique()
                .HasDatabaseName("IX_DeadLetterMessages_OriginalMessageId_Unique");
        });
    }

    // ----- Value converters -----

    /// <summary>
    /// Loss-free reinterpretation cast between an unsigned Discord snowflake
    /// and a signed 64-bit integer. Snowflakes occupy the lower 63 bits so
    /// the high bit is always 0; <c>(long)value</c> and <c>(ulong)value</c>
    /// round-trip exactly for every Discord-issued id.
    /// </summary>
    internal static readonly ValueConverter<ulong, long> SnowflakeConverter =
        new(v => (long)v, v => (ulong)v);

    /// <summary>Nullable companion to <see cref="SnowflakeConverter"/>.</summary>
    internal static readonly ValueConverter<ulong?, long?> NullableSnowflakeConverter =
        new(
            v => v.HasValue ? (long)v.Value : (long?)null,
            v => v.HasValue ? (ulong)v.Value : (ulong?)null);

    private static readonly ValueConverter<IReadOnlyList<ulong>, string> AllowedRoleIdsConverter =
        new(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => DeserializeRoles(v));

    private static IReadOnlyList<ulong> DeserializeRoles(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return Array.Empty<ulong>();
        }

        var parsed = JsonSerializer.Deserialize<ulong[]>(raw, JsonOptions) ?? Array.Empty<ulong>();
        return new ReadOnlyCollection<ulong>(parsed);
    }

    private static readonly ValueComparer<IReadOnlyList<ulong>> AllowedRoleIdsComparer = new(
        (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
        v => v == null ? 0 : v.Aggregate(0, (h, x) => HashCode.Combine(h, x)),
        v => (IReadOnlyList<ulong>)new ReadOnlyCollection<ulong>(v.ToArray()));

    private static readonly ValueConverter<IReadOnlyDictionary<string, IReadOnlyList<ulong>>?, string?>
        CommandRestrictionsConverter = new(
            v => v == null ? null : JsonSerializer.Serialize(SerializeRestrictions(v), JsonOptions),
            v => DeserializeRestrictions(v));

    private static Dictionary<string, ulong[]> SerializeRestrictions(
        IReadOnlyDictionary<string, IReadOnlyList<ulong>> source)
    {
        var copy = new Dictionary<string, ulong[]>(source.Count, StringComparer.Ordinal);
        foreach (var kv in source)
        {
            copy[kv.Key] = kv.Value.ToArray();
        }

        return copy;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ulong>>? DeserializeRestrictions(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, ulong[]>>(raw, JsonOptions);
        if (parsed is null)
        {
            return null;
        }

        var copy = new Dictionary<string, IReadOnlyList<ulong>>(parsed.Count, StringComparer.Ordinal);
        foreach (var kv in parsed)
        {
            copy[kv.Key] = new ReadOnlyCollection<ulong>(kv.Value);
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<ulong>>(copy);
    }

    private static readonly ValueComparer<IReadOnlyDictionary<string, IReadOnlyList<ulong>>?>
        CommandRestrictionsComparer = new(
            (a, b) => RestrictionsEqual(a, b),
            v => v == null ? 0 : v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value.Count)),
            v => v == null ? null : DeserializeRestrictions(JsonSerializer.Serialize(SerializeRestrictions(v), JsonOptions)));

    private static bool RestrictionsEqual(
        IReadOnlyDictionary<string, IReadOnlyList<ulong>>? a,
        IReadOnlyDictionary<string, IReadOnlyList<ulong>>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var other))
            {
                return false;
            }

            if (!kv.Value.SequenceEqual(other))
            {
                return false;
            }
        }

        return true;
    }
}
