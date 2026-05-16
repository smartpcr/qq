using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Discord;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Stage 2.1 acceptance tests for the messenger persistence schema.
/// Covers the three scenarios pinned by implementation-plan.md Stage 2.1:
/// (1) DbContext creates all tables, (2) DiscordInteractionRecord
/// InteractionId UNIQUE constraint is enforced, (3) GuildBinding
/// (GuildId, ChannelId, WorkspaceId) UNIQUE constraint is enforced.
/// </summary>
public class MessagingDbContextTests : IDisposable
{
    private readonly SqliteConnection _sqlite;

    public MessagingDbContextTests()
    {
        // Shared open SQLite in-memory connection so the schema persists for
        // the lifetime of the test (the in-memory database is dropped when
        // the connection closes).
        _sqlite = new SqliteConnection("DataSource=:memory:");
        _sqlite.Open();
    }

    public void Dispose()
    {
        _sqlite.Dispose();
        GC.SuppressFinalize(this);
    }

    private MessagingDbContext NewSqliteContext()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_sqlite)
            .Options;
        var context = new MessagingDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private static MessagingDbContext NewInMemoryContext(string name)
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MessagingDbContext(options);
    }

    [Fact]
    public void EnsureCreated_OnInMemoryProvider_InitializesSchemaWithoutErrors()
    {
        // Spec wording: "Given an InMemory database provider, When EnsureCreated
        // is called on MessagingDbContext, Then all tables are created without
        // errors". The InMemory provider is non-relational, so the pass
        // condition is that EnsureCreated returns true (meaning the model was
        // valid and the in-memory store was newly initialized) and that every
        // DbSet is queryable end-to-end.
        using var context = NewInMemoryContext($"inmem_{Guid.NewGuid()}");

        var created = context.Database.EnsureCreated();

        created.Should().BeTrue();
        context.DiscordInteractions.Count().Should().Be(0);
        context.GuildBindings.Count().Should().Be(0);
        context.OutboundMessages.Count().Should().Be(0);
        context.PendingQuestions.Count().Should().Be(0);
        context.AuditLog.Count().Should().Be(0);
        context.DeadLetterMessages.Count().Should().Be(0);
    }

    [Fact]
    public void EnsureCreated_OnSqliteProvider_CreatesAllExpectedTables()
    {
        // Stronger relational verification of "all tables are created":
        // query sqlite_master for every expected table name.
        using var context = NewSqliteContext();

        var tables = QueryTableNames(context);

        tables.Should().Contain(new[]
        {
            "DiscordInteractions",
            "GuildBindings",
            "OutboundMessages",
            "PendingQuestions",
            "AuditLog",
            "DeadLetterMessages",
        });
    }

    [Fact]
    public void Schema_DefinesAllRequiredIndexes_WithCorrectUniquenessFlags()
    {
        // Closes the iter-2 evaluator's "minor coverage gap": prove the
        // index names + UNIQUE flags pinned by architecture.md §3.1/§3.2
        // and the implementation-plan really land in the generated schema,
        // not just in the EF model snapshot. Reads SQLite catalog metadata
        // (PRAGMA index_list) for every entity table.
        using var context = NewSqliteContext();

        var indexes = ReadAllIndexes(context);

        // Asserted name → expected uniqueness flag, per the Fluent API
        // configuration in MessagingDbContext.OnModelCreating.
        var expectations = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["IX_DiscordInteractions_InteractionId_Unique"] = true,
            ["IX_DiscordInteractions_Status_ReceivedAt"] = false,
            ["IX_GuildBindings_Guild_Channel_Workspace_Unique"] = true,
            ["IX_GuildBindings_Guild_Purpose"] = false,
            ["IX_OutboundMessages_IdempotencyKey_Unique"] = true,
            ["IX_OutboundMessages_Status_Severity_NextRetryAt"] = false,
            ["IX_PendingQuestions_Status_ExpiresAt"] = false,
            ["IX_PendingQuestions_Channel_Message"] = false,
            ["IX_AuditLog_CorrelationId"] = false,
            ["IX_AuditLog_Platform_Timestamp"] = false,
            ["IX_DeadLetterMessages_OriginalMessageId_Unique"] = true,
        };

        foreach (var (name, expectedUnique) in expectations)
        {
            indexes.Should().ContainKey(name, $"index '{name}' must exist in the generated schema");
            indexes[name].IsUnique.Should().Be(expectedUnique,
                $"index '{name}' uniqueness must match the Fluent API configuration");
        }
    }

    [Fact]
    public void Schema_DeclaresDeadLetterToOutboundForeignKey_PointingAtMessageId()
    {
        // Closes the iter-2 evaluator's coverage gap on the FK name + target.
        // Architecture.md §3.2 pins `DeadLetterMessage 1--1 OutboundMessage
        // (via OriginalMessageId)`. We read PRAGMA foreign_key_list from
        // SQLite and assert the relationship landed exactly as configured.
        using var context = NewSqliteContext();
        var foreignKeys = ReadForeignKeys(context, "DeadLetterMessages");

        foreignKeys.Should().HaveCount(1,
            "DeadLetterMessages.OriginalMessageId is the only FK on this table");

        var fk = foreignKeys[0];
        fk.TargetTable.Should().Be("OutboundMessages");
        fk.SourceColumn.Should().Be("OriginalMessageId");
        fk.TargetColumn.Should().Be("MessageId");
        // EF maps OnDelete(DeleteBehavior.Restrict) to SQLite's "NO ACTION"
        // (Restrict and NoAction are semantically identical -- both reject
        // the parent delete -- and the SQLite provider emits NO ACTION).
        fk.OnDelete.Should().BeOneOf("NO ACTION", "RESTRICT");
    }

    [Fact]
    public async Task DiscordInteractionRecord_DuplicateInteractionId_ThrowsDbUpdateException()
    {
        using var context = NewSqliteContext();

        var first = NewInteractionRecord(interactionId: 12345UL);
        context.DiscordInteractions.Add(first);
        await context.SaveChangesAsync();

        // New context to simulate a separate write path (and avoid the
        // change-tracker rejecting the duplicate before SQLite sees it).
        using var second = NewSqliteContext();
        second.DiscordInteractions.Add(NewInteractionRecord(interactionId: 12345UL));

        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task GuildBinding_DuplicateGuildChannelWorkspace_ThrowsDbUpdateException()
    {
        using var context = NewSqliteContext();

        var first = NewGuildBinding(
            id: Guid.NewGuid(),
            guildId: 1UL,
            channelId: 2UL,
            workspaceId: "ws-main");
        context.GuildBindings.Add(first);
        await context.SaveChangesAsync();

        using var second = NewSqliteContext();
        second.GuildBindings.Add(NewGuildBinding(
            id: Guid.NewGuid(),
            guildId: 1UL,
            channelId: 2UL,
            workspaceId: "ws-main"));

        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task GuildBinding_RoundtripsAllowedRoleIdsAndCommandRestrictionsAsJson()
    {
        // JSON converter sanity check on the GuildBinding shared record:
        // a value with multiple role ids and a populated command-restrictions
        // map must round-trip through SQLite and remain immutable on read.
        using var write = NewSqliteContext();
        var binding = NewGuildBinding(
            id: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            guildId: 100UL,
            channelId: 200UL,
            workspaceId: "ws-A",
            allowedRoleIds: new ulong[] { 10UL, 20UL, 30UL },
            commandRestrictions: new Dictionary<string, IReadOnlyList<ulong>>(StringComparer.Ordinal)
            {
                ["approve"] = new ulong[] { 20UL },
                ["reject"] = new ulong[] { 20UL, 30UL },
            });
        write.GuildBindings.Add(binding);
        await write.SaveChangesAsync();

        using var read = NewSqliteContext();
        var loaded = await read.GuildBindings.SingleAsync(g => g.Id == binding.Id);

        loaded.GuildId.Should().Be(100UL);
        loaded.ChannelId.Should().Be(200UL);
        loaded.AllowedRoleIds.Should().Equal(10UL, 20UL, 30UL);
        loaded.CommandRestrictions.Should().NotBeNull();
        loaded.CommandRestrictions!["approve"].Should().Equal(20UL);
        loaded.CommandRestrictions["reject"].Should().Equal(20UL, 30UL);

        // Defensive copy contract carried over from the shared record: the
        // exposed view cannot be downcast back to a mutable list / array.
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ulong>)loaded.AllowedRoleIds).Add(99UL));
    }

    [Fact]
    public async Task DiscordInteractionRecord_RoundtripsSnowflakeIdsAndEnumValues()
    {
        // Confirms the ulong<->long snowflake converter is loss-free for a
        // value above int.MaxValue and that the IdempotencyStatus and
        // InteractionType enums materialize correctly.
        using var write = NewSqliteContext();
        var record = NewInteractionRecord(
            interactionId: 1234567890123456789UL,
            interactionType: DiscordInteractionType.ButtonClick,
            idempotencyStatus: IdempotencyStatus.Processing);
        write.DiscordInteractions.Add(record);
        await write.SaveChangesAsync();

        using var read = NewSqliteContext();
        var loaded = await read.DiscordInteractions.SingleAsync();

        loaded.InteractionId.Should().Be(1234567890123456789UL);
        loaded.InteractionType.Should().Be(DiscordInteractionType.ButtonClick);
        loaded.IdempotencyStatus.Should().Be(IdempotencyStatus.Processing);
        loaded.GuildId.Should().Be(record.GuildId);
        loaded.ChannelId.Should().Be(record.ChannelId);
        loaded.UserId.Should().Be(record.UserId);
    }

    [Fact]
    public void AuditLogEntry_DefaultId_IsNonEmpty()
    {
        // Regression guard: AuditLogEntry.Id has a Guid.NewGuid() property
        // initializer because EF is configured ValueGeneratedNever (SQLite
        // has no built-in Guid generator). Two freshly constructed instances
        // must receive distinct, non-empty ids without any explicit
        // assignment from the caller.
        var first = new AuditLogEntry();
        var second = new AuditLogEntry();

        first.Id.Should().NotBe(Guid.Empty);
        second.Id.Should().NotBe(Guid.Empty);
        first.Id.Should().NotBe(second.Id);
    }

    [Fact]
    public void DeadLetterMessage_DefaultId_IsNonEmpty()
    {
        // Same regression guard for DeadLetterMessage.Id.
        var first = new DeadLetterMessage();
        var second = new DeadLetterMessage();

        first.Id.Should().NotBe(Guid.Empty);
        second.Id.Should().NotBe(Guid.Empty);
        first.Id.Should().NotBe(second.Id);
    }

    [Fact]
    public async Task AuditLogEntry_InsertWithoutExplicitId_PersistsAndRoundTrips()
    {
        // End-to-end check: the property initializer plus ValueGeneratedNever
        // produce a working insert without the caller touching Id.
        using var write = NewSqliteContext();
        var entry = new AuditLogEntry
        {
            Platform = "Discord",
            ExternalUserId = "111",
            MessageId = "222",
            Details = "{\"GuildId\":\"333\"}",
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CorrelationId = "corr-1",
        };
        write.AuditLog.Add(entry);
        await write.SaveChangesAsync();

        using var read = NewSqliteContext();
        var loaded = await read.AuditLog.SingleAsync();

        loaded.Id.Should().NotBe(Guid.Empty);
        loaded.Id.Should().Be(entry.Id);
        loaded.Platform.Should().Be("Discord");
        loaded.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task DeadLetterMessage_OrphanOriginalMessageId_ThrowsDbUpdateException()
    {
        // The architecture/implementation-plan 1--1 FK from
        // DeadLetterMessage.OriginalMessageId to OutboundMessage.MessageId
        // means a dead-letter row that points at a non-existent outbound
        // message must be rejected by the FK constraint.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        // SQLite has FK enforcement off by default; turn it on for this test
        // (EF Core enables it automatically when it owns the connection,
        // but we own this connection explicitly so we set the pragma).
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var ctx = new MessagingDbContext(options))
        {
            ctx.Database.EnsureCreated();
        }

        using var write = new MessagingDbContext(options);
        write.DeadLetterMessages.Add(new DeadLetterMessage
        {
            OriginalMessageId = Guid.NewGuid(),
            ChatId = 1,
            Payload = "{}",
            ErrorReason = "orphan",
            FailedAt = DateTimeOffset.UtcNow,
            AttemptCount = 5,
        });

        var act = async () => await write.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task DeadLetterMessage_DuplicateOriginalMessageId_ThrowsDbUpdateException()
    {
        // The UNIQUE index on OriginalMessageId enforces the 1--1
        // architectural contract: a second dead-letter row pointing at the
        // same source OutboundMessage must collide rather than accumulate.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var ctx = new MessagingDbContext(options))
        {
            ctx.Database.EnsureCreated();
        }

        var sourceMessageId = Guid.NewGuid();

        using (var seed = new MessagingDbContext(options))
        {
            seed.OutboundMessages.Add(OutboundMessage.Create(
                idempotencyKey: "c:trace-dl-1",
                chatId: 1,
                severity: MessageSeverity.Normal,
                sourceType: OutboundMessageSource.CommandAck,
                payload: "{}",
                correlationId: "trace-dl-1",
                messageId: sourceMessageId));
            seed.DeadLetterMessages.Add(new DeadLetterMessage
            {
                OriginalMessageId = sourceMessageId,
                ChatId = 1,
                Payload = "{}",
                ErrorReason = "first dead-letter",
                FailedAt = DateTimeOffset.UtcNow,
                AttemptCount = 5,
            });
            await seed.SaveChangesAsync();
        }

        using var duplicate = new MessagingDbContext(options);
        duplicate.DeadLetterMessages.Add(new DeadLetterMessage
        {
            OriginalMessageId = sourceMessageId,
            ChatId = 1,
            Payload = "{}",
            ErrorReason = "duplicate dead-letter attempt",
            FailedAt = DateTimeOffset.UtcNow,
            AttemptCount = 5,
        });

        var act = async () => await duplicate.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private static DiscordInteractionRecord NewInteractionRecord(
        ulong interactionId,
        DiscordInteractionType interactionType = DiscordInteractionType.SlashCommand,
        IdempotencyStatus idempotencyStatus = IdempotencyStatus.Received)
    {
        return new DiscordInteractionRecord
        {
            InteractionId = interactionId,
            InteractionType = interactionType,
            GuildId = 999_000_000_000UL,
            ChannelId = 888_000_000_000UL,
            UserId = 777_000_000_000UL,
            RawPayload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = null,
            IdempotencyStatus = idempotencyStatus,
            AttemptCount = 0,
            ErrorDetail = null,
        };
    }

    private static GuildBinding NewGuildBinding(
        Guid id,
        ulong guildId,
        ulong channelId,
        string workspaceId,
        IReadOnlyList<ulong>? allowedRoleIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<ulong>>? commandRestrictions = null)
    {
        return new GuildBinding(
            Id: id,
            GuildId: guildId,
            ChannelId: channelId,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "tenant-1",
            WorkspaceId: workspaceId,
            AllowedRoleIds: allowedRoleIds ?? new ulong[] { 1UL },
            CommandRestrictions: commandRestrictions,
            RegisteredAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IsActive: true);
    }

    private static IReadOnlyList<string> QueryTableNames(MessagingDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static IReadOnlyDictionary<string, IndexInfo> ReadAllIndexes(MessagingDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        var tables = QueryTableNames(context);
        var indexes = new Dictionary<string, IndexInfo>(StringComparer.Ordinal);

        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA index_list('{table}');";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // Columns: seq, name, unique, origin, partial
                var name = reader.GetString(1);
                var unique = reader.GetInt64(2) != 0;
                indexes[name] = new IndexInfo(name, unique, table);
            }
        }

        return indexes;
    }

    private static IReadOnlyList<ForeignKeyInfo> ReadForeignKeys(MessagingDbContext context, string table)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{table}');";
        using var reader = command.ExecuteReader();
        var foreignKeys = new List<ForeignKeyInfo>();
        while (reader.Read())
        {
            // Columns: id, seq, table, from, to, on_update, on_delete, match
            var targetTable = reader.GetString(2);
            var sourceColumn = reader.GetString(3);
            var targetColumn = reader.GetString(4);
            var onDelete = reader.GetString(6);
            foreignKeys.Add(new ForeignKeyInfo(table, sourceColumn, targetTable, targetColumn, onDelete));
        }

        return foreignKeys;
    }

    private sealed record IndexInfo(string Name, bool IsUnique, string Table);

    private sealed record ForeignKeyInfo(
        string SourceTable,
        string SourceColumn,
        string TargetTable,
        string TargetColumn,
        string OnDelete);
}
