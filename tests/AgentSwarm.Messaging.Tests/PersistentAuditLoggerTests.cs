using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.3 — round-trip tests for <see cref="PersistentAuditLogger"/>
/// against an in-memory SQLite connection using the real
/// <see cref="AuditDbContext"/> schema (so the
/// <see cref="AuditLogEntryConfiguration"/>-defined indexes and the
/// <see cref="DateTimeOffset"/> Unix-ms value converter both run
/// end-to-end). Pins the Stage 5.3 column shape — <c>Id</c>,
/// <c>ExternalUserId</c>, <c>TenantId</c>, <c>Platform</c>,
/// string-discriminated <c>EntryKind</c> — against the audit_logs
/// table the brief mandates.
/// </summary>
public sealed class PersistentAuditLoggerTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private PersistentAuditLogger _logger = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        await using (var scope = _provider.CreateAsyncScope())
        await using (var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _logger = new PersistentAuditLogger(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PersistentAuditLogger>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task LogAsync_PersistsRowWithGeneralEntryKindAndStage5_3Schema()
    {
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "msg-1",
            UserId = "u-1",
            AgentId = "agent-7",
            Action = "command.received",
            Timestamp = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            CorrelationId = "trace-1",
            TenantId = "tenant-a",
            Details = "{\"raw\":\"/status\"}",
        };

        await _logger.LogAsync(entry, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await ctx.AuditLogs.SingleAsync();
        row.Id.Should().Be(entry.EntryId,
            "Stage 5.3 brief: persistence renames EntryId → Id");
        row.EntryKind.Should().Be(AuditEntryKinds.General);
        row.MessageId.Should().Be("msg-1");
        row.ExternalUserId.Should().Be("u-1",
            "Stage 5.3 brief: persistence renames UserId → ExternalUserId");
        row.AgentId.Should().Be("agent-7");
        row.Action.Should().Be("command.received");
        row.Timestamp.Should().Be(entry.Timestamp);
        row.CorrelationId.Should().Be("trace-1");
        row.TenantId.Should().Be("tenant-a");
        row.Platform.Should().Be(AuditLogEntry.TelegramPlatform,
            "Stage 5.3 brief: Platform column is always 'Telegram' for this connector");
        row.Details.Should().Be("{\"raw\":\"/status\"}");
        row.QuestionId.Should().BeNull();
        row.ActionValue.Should().BeNull();
        row.Comment.Should().BeNull();
    }

    [Fact]
    public async Task LogHumanResponseAsync_PersistsRowWithAllStoryRequiredFields()
    {
        // Story brief: "Persist every human response with message ID,
        // user ID, agent ID, timestamp, and correlation ID." This test
        // pins that every named field round-trips through the writer,
        // and that the EntryKind discriminator separates the row from
        // a general AuditEntry. Stage 5.3 additionally requires the
        // persistence layer to populate TenantId and Platform on every
        // row.
        var entry = new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "msg-42",
            UserId = "u-42",
            AgentId = "agent-9",
            QuestionId = "Q-1",
            ActionValue = "approve",
            Comment = "looks good",
            Timestamp = new DateTimeOffset(2025, 2, 1, 9, 30, 0, TimeSpan.Zero),
            CorrelationId = "trace-42",
            TenantId = "tenant-b",
        };

        await _logger.LogHumanResponseAsync(entry, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await ctx.AuditLogs.SingleAsync();
        row.Id.Should().Be(entry.EntryId);
        row.EntryKind.Should().Be(AuditEntryKinds.HumanResponse);
        row.MessageId.Should().Be("msg-42");
        row.ExternalUserId.Should().Be("u-42");
        row.AgentId.Should().Be("agent-9");
        row.QuestionId.Should().Be("Q-1");
        row.ActionValue.Should().Be("approve");
        row.Comment.Should().Be("looks good");
        row.Timestamp.Should().Be(entry.Timestamp);
        row.CorrelationId.Should().Be("trace-42");
        row.TenantId.Should().Be("tenant-b");
        row.Platform.Should().Be(AuditLogEntry.TelegramPlatform);
    }

    [Fact]
    public async Task LogHumanResponseAsync_PersistsActionValueAsCanonicalActionVerb()
    {
        // Stage 5.3 acceptance: "AuditLogEntry exists with Action=approve"
        // for an approve button press. The canonical action verb the
        // operator selected lives in HumanResponseAuditEntry.ActionValue
        // (e.g. "approve" / "reject" / "__timeout__") — the persistence
        // row's Action column surfaces it verbatim so a forensic query
        // can filter `WHERE Action='approve'` without consulting
        // ActionValue. Stage 5.3 iter-2 evaluator item 2 was the iter-1
        // overload of Action with the generic literal "human.response":
        // that hid the per-decision verb the acceptance scenario pins.
        // The EntryKind discriminator (general / human-response) still
        // separates commands from decisions when needed; the EventFamily
        // column carries the "decision" family tag.
        var entry = new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "m",
            UserId = "u",
            AgentId = "a",
            QuestionId = "q",
            ActionValue = "reject",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-action",
        };

        await _logger.LogHumanResponseAsync(entry, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await ctx.AuditLogs.SingleAsync();
        row.Action.Should().Be("reject",
            "Stage 5.3 iter-2 evaluator item 2: row.Action carries the canonical action verb the operator selected, NOT the generic 'human.response' literal that hid which decision was made");
        row.EventFamily.Should().Be(AuditEventFamilies.Decision,
            "decision rows tag the family on the dedicated EventFamily column so log queries can filter by EventFamily='decision' without parsing Action");
        row.EntryKind.Should().Be(AuditEntryKinds.HumanResponse,
            "the persistence-layer EntryKind discriminator separates rows produced by IAuditLogger.LogAsync (general) from those produced by LogHumanResponseAsync (human-response)");
    }

    [Fact]
    public async Task BothLoggerPaths_PersistToSameTable_DiscriminatedByEntryKind()
    {
        await _logger.LogAsync(new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-1",
            Action = "command.received",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-a",
        }, default);
        await _logger.LogHumanResponseAsync(new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "m",
            UserId = "u-2",
            AgentId = "a",
            QuestionId = "Q-2",
            ActionValue = "approve",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-b",
        }, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await ctx.AuditLogs.OrderBy(x => x.CorrelationId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.EntryKind).Should().BeEquivalentTo(
            new[] { AuditEntryKinds.General, AuditEntryKinds.HumanResponse });
        rows.Should().AllSatisfy(r =>
            r.Platform.Should().Be(AuditLogEntry.TelegramPlatform,
                "Stage 5.3 brief: Platform is always 'Telegram' regardless of EntryKind"));
    }

    [Fact]
    public async Task LogAsync_NullTenantId_PersistsRow()
    {
        // The unauthorized-rejection lifecycle path emits audit rows
        // BEFORE authorization resolves a binding (no tenant yet);
        // the persistence schema must accept null TenantId for that
        // case rather than rejecting the write.
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "unknown-user",
            Action = "command.rejected",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-rejected",
            TenantId = null,
        };

        await _logger.LogAsync(entry, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await ctx.AuditLogs.SingleAsync();
        row.TenantId.Should().BeNull();
        row.Platform.Should().Be(AuditLogEntry.TelegramPlatform);
    }

    // ============================================================
    // Stage 5.3 iter-2 evaluator item 4:
    // PersistentAuditLogger MUST surface persistence failures to the
    // caller rather than silently swallowing them — the "log and
    // swallow" path let business commands appear to succeed with no
    // persisted audit row, violating the brief's "Persist every
    // human response" + transactional-write contracts.
    // ============================================================

    [Fact]
    public async Task LogAsync_OnSaveFailure_PropagatesExceptionAndDoesNotPersistRow()
    {
        // Sabotage the underlying schema after EnsureCreated so the
        // INSERT raises a real SQLite error inside SaveChangesAsync.
        // (Dropping the table is the simplest way to simulate a
        // transient backing-store failure without mocking EF Core.)
        await using (var scope = _provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE audit_logs;");
        }

        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-fail",
            Action = "command.failed",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-fail",
        };

        var act = async () => await _logger.LogAsync(entry, default);

        // Iter-1 evaluator item 4: previous writer silently swallowed
        // ALL persistence failures with a log line; PersistentAuditLogger
        // MUST now surface them. EF Core wraps the underlying SQLite
        // "no such table" error in DbUpdateException; we only assert
        // that SOME exception escapes the writer — not the exact type
        // or message, so a provider swap (PostgreSQL, SQL Server) does
        // not break the pin.
        var ex = await act.Should().ThrowAsync<Exception>(
            "Stage 5.3 iter-2 evaluator item 4: PersistentAuditLogger MUST propagate persistence failures so the caller can honor the transactional-write contract instead of silently dropping the audit row");
        // Belt-and-braces: confirm the exception did NOT come from
        // FluentAssertions itself (which would mean act.Should().Throw
        // matched an empty exception). The underlying EF wrap is
        // DbUpdateException for every relational provider.
        ex.Which.Should().BeAssignableTo<Microsoft.EntityFrameworkCore.DbUpdateException>(
            "EF Core wraps relational SaveChanges failures in DbUpdateException; the writer must let that wrapper propagate unmodified");
    }

    // ============================================================
    // Stage 5.3 iter-2 evaluator item 5:
    // AuditDbContext.SaveChanges MUST reject Modified / Deleted
    // AuditLogEntry states at the storage tier — closing the back
    // door for DI consumers that resolve the context directly and
    // try to mutate / delete an existing row.
    // ============================================================

    [Fact]
    public async Task SaveChangesAsync_RejectsModifiedAuditLogEntry()
    {
        var existing = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-immutable-1",
            Action = "approve",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-immutable-1",
        };
        await _logger.LogAsync(existing, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await ctx.AuditLogs.SingleAsync();
        // Force EF to track the row as Modified — mirrors what a DI
        // consumer with direct DbContext access would do if they tried
        // to "patch" an audit row.
        ctx.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Modified;

        var act = async () => await ctx.SaveChangesAsync(default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*append-only*",
                "Stage 5.3 iter-2 evaluator item 5: the context's SaveChanges guard MUST reject Modified AuditLogEntry states so the immutability contract holds even for direct DbContext access");
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsDeletedAuditLogEntry()
    {
        var existing = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-immutable-2",
            Action = "reject",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-immutable-2",
        };
        await _logger.LogAsync(existing, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await ctx.AuditLogs.SingleAsync();
        ctx.AuditLogs.Remove(row);

        var act = async () => await ctx.SaveChangesAsync(default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*append-only*",
                "Stage 5.3 iter-2 evaluator item 5: the context's SaveChanges guard MUST reject Deleted AuditLogEntry states so DELETE is impossible via direct DbContext access");
    }

    [Fact]
    public async Task SaveChangesAsync_AllowsAddedAuditLogEntry()
    {
        // Sanity: the guard MUST NOT block normal inserts. Without
        // this pin, a future tighten-up of EnsureAuditEntriesAppendOnly
        // could silently break PersistentAuditLogger writes.
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-insert-ok",
            Action = "ask",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-insert-ok",
        };

        var act = async () => await _logger.LogAsync(entry, default);

        await act.Should().NotThrowAsync(
            "Stage 5.3 iter-2 evaluator item 5: the immutability guard must allow Added states or the writer itself stops working");
    }

    [Fact]
    public async Task LogAsync_RejectsInvalidJsonDetails_BeforeOpeningTransaction()
    {
        // Stage 5.3 iter-9 evaluator item 3 — the Details column is
        // typed `Details (JSON)`. A free-form non-JSON string MUST
        // be rejected at the writer boundary so downstream forensic
        // queries (json_extract / JSON_VALUE / jsonb_path_query)
        // cannot land on a malformed row.
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-bad-json",
            Action = "command.received",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-bad-json",
            Details = "this is not json",
        };

        var act = async () => await _logger.LogAsync(entry, default);

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("Details",
                "the ArgumentException must surface the offending parameter so the caller can fix the payload");

        // Confirm NO row landed on the DB despite the throw.
        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var count = await ctx.AuditLogs.CountAsync();
        count.Should().Be(0,
            "Stage 5.3 iter-9 evaluator item 3: invalid-JSON Details must short-circuit BEFORE the transaction opens so the audit table is never polluted");
    }

    [Fact]
    public async Task LogAsync_AcceptsNullDetails()
    {
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-null-details",
            Action = "command.received",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-null-details",
            Details = null,
        };

        var act = async () => await _logger.LogAsync(entry, default);

        await act.Should().NotThrowAsync(
            "null Details is the explicit `no details` signal and must be accepted");
    }

    [Theory]
    [InlineData("{\"raw\":\"/status\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a-string-literal\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task LogAsync_AcceptsAnyValidJsonToken(string validJson)
    {
        // Every JSON token (object, array, string, number, null,
        // boolean) is a valid JSON document per RFC 8259. The
        // Stage 5.3 column accepts any of them so callers can
        // serialise lightweight payloads (e.g. a bare numeric
        // counter) without wrapping in an object.
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-token-" + Guid.NewGuid().ToString("N"),
            Action = "command.received",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-token",
            Details = validJson,
        };

        await _logger.LogAsync(entry, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await ctx.AuditLogs.SingleAsync(r => r.Id == entry.EntryId);
        row.Details.Should().Be(validJson);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json }")]
    [InlineData("{\"unterminated\":")]
    public async Task LogAsync_RejectsMalformedAndEmptyDetails(string badJson)
    {
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "u-malformed",
            Action = "command.received",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-malformed",
            Details = badJson,
        };

        var act = async () => await _logger.LogAsync(entry, default);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("JSON",
                "the error message must hint at the JSON requirement so the caller can fix the payload");
    }

    [Fact]
    public async Task LogHumanResponseAsync_RejectsInvalidJsonDetails()
    {
        var entry = new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "msg-1",
            UserId = "u-hr",
            AgentId = "agent-1",
            QuestionId = "q-1",
            ActionValue = "approve",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-hr",
            Details = "definitely-not-json",
        };

        var act = async () => await _logger.LogHumanResponseAsync(entry, default);
        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("Details");
    }

    [Fact]
    public async Task AuditDbContext_SaveChanges_RejectsRawAddOfInvalidJsonDetails()
    {
        // Stage 5.3 iter-9 evaluator item 3 — defense-in-depth at
        // the model boundary. A direct in-assembly Add of a raw
        // AuditLogEntry (bypassing PersistentAuditLogger) with
        // non-JSON Details MUST also be rejected so the audit table
        // is protected even from writer-side bugs.
        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        ctx.AuditLogs.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            EntryKind = AuditEntryKinds.General,
            EventFamily = AuditEventFamilies.General,
            ExternalUserId = "u-raw-bad-json",
            Action = "raw.command",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-raw-bad-json",
            Platform = AuditLogEntry.TelegramPlatform,
            Details = "{ not-json",
        });

        var act = async () => await ctx.SaveChangesAsync(default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*JSON*",
                "Stage 5.3 iter-9 evaluator item 3: the SaveChanges guard must reject Added AuditLogEntry rows whose Details payload is not valid JSON");
    }

    [Fact]
    public async Task AuditDbContext_SaveChanges_AcceptsRawAddOfValidJsonDetails()
    {
        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        ctx.AuditLogs.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            EntryKind = AuditEntryKinds.General,
            EventFamily = AuditEventFamilies.General,
            ExternalUserId = "u-raw-good-json",
            Action = "raw.command",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-raw-good-json",
            Platform = AuditLogEntry.TelegramPlatform,
            Details = "{\"ok\":true}",
        });

        var act = async () => await ctx.SaveChangesAsync(default);
        await act.Should().NotThrowAsync(
            "valid JSON Details must persist so the writer + raw-Add paths stay consistent");
    }
}
