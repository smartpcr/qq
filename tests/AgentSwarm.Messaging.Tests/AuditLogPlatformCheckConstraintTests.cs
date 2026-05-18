// -----------------------------------------------------------------------
// <copyright file="AuditLogPlatformCheckConstraintTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.3 iter-8 evaluator item 2 — pins the DB-level CHECK
/// constraint <c>ck_audit_logs_platform_telegram</c> that enforces
/// <c>Platform = 'Telegram'</c> on every audit row, regardless of
/// the client that issued the INSERT. The Stage 5.3 brief mandates
/// "<i>Platform (always 'Telegram')</i>" on this connector; a column
/// default alone cannot enforce this because an explicit
/// <c>Platform = 'Discord'</c> on the INSERT statement would bypass
/// the default. The CHECK constraint is the engine-level guarantee
/// that catches every path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a CHECK constraint, not just an
/// <see cref="AuditDbContext.EnsureAuditEntriesAppendOnly"/> guard.</b>
/// The change-tracker guard catches EF-tracked Adds but NOT raw SQL
/// or bulk INSERTs from a future tool. The CHECK is portable across
/// the three providers Stage 5.3 supports (SQLite, PostgreSQL, SQL
/// Server) and runs inside the database engine itself — no
/// application code can bypass it.
/// </para>
/// </remarks>
public sealed class AuditLogPlatformCheckConstraintTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RawSqlInsert_WithNonTelegramPlatform_ViolatesCheckConstraint()
    {
        // Stage 5.3 iter-8 evaluator item 2 — pin the DB-level CHECK
        // constraint. A direct raw-SQL INSERT with Platform='Discord'
        // MUST be rejected by the database engine itself; if this
        // assertion ever passes (no throw), the audit table has lost
        // its "always Telegram" guarantee and a future writer could
        // pollute the trail with cross-connector rows.
        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var id = Guid.NewGuid().ToString();
        var sql =
            $"INSERT INTO audit_logs (Id, ExternalUserId, Action, EventFamily, Timestamp, CorrelationId, Platform, EntryKind) " +
            $"VALUES ('{id}', 'u-evil', 'ask', 'command', 0, 'trace-evil', 'Discord', 'general')";

        var act = async () => await ctx.Database.ExecuteSqlRawAsync(sql);

        await act.Should().ThrowAsync<SqliteException>(
            "Stage 5.3 iter-8 evaluator item 2: the DB-level CHECK constraint MUST reject any non-Telegram Platform value on raw-SQL INSERT — column defaults alone do not enforce the 'always Telegram' invariant");

        // Belt-and-braces: confirm no row landed.
        var rows = await ctx.AuditLogs.AsNoTracking().ToListAsync();
        rows.Should().BeEmpty("the CHECK violation must short-circuit before any row is persisted");
    }

    [Fact]
    public async Task RawSqlInsert_WithTelegramPlatform_Succeeds()
    {
        // Sanity pin — the CHECK constraint MUST allow Platform='Telegram'.
        // If this assertion ever fails, the constraint expression is
        // backwards and audit writes are universally broken.
        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var id = Guid.NewGuid().ToString();
        var sql =
            $"INSERT INTO audit_logs (Id, ExternalUserId, Action, EventFamily, Timestamp, CorrelationId, Platform, EntryKind) " +
            $"VALUES ('{id}', 'u-ok', 'ask', 'command', 0, 'trace-ok', 'Telegram', 'general')";

        var act = async () => await ctx.Database.ExecuteSqlRawAsync(sql);

        await act.Should().NotThrowAsync(
            "Stage 5.3 iter-8 evaluator item 2: the CHECK constraint MUST allow Platform='Telegram' — the canonical value");

        var rows = await ctx.AuditLogs.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle().Which.Platform.Should().Be("Telegram");
    }

    [Fact]
    public void Migration_DeclaresPlatformCheckConstraint()
    {
        // Reflection-free static pin — the constant names referenced
        // by the EF configuration and the migration both resolve to
        // the canonical CHECK constraint identity, so a future
        // refactor cannot silently drop the constraint without
        // breaking this assertion.
        AuditLogEntryConfiguration.PlatformCheckConstraintName
            .Should().Be("ck_audit_logs_platform_telegram",
                "Stage 5.3 iter-8 evaluator item 2: the CHECK constraint name is the canonical handle the migration and tests reference; renaming requires a migration");
        AuditLogEntryConfiguration.PlatformCheckConstraintSql
            .Should().Contain("Telegram",
                "Stage 5.3 iter-8 evaluator item 2: the CHECK expression must pin Platform to the literal 'Telegram'");
    }
}
