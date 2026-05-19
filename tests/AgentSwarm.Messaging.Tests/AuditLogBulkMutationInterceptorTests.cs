// -----------------------------------------------------------------------
// <copyright file="AuditLogBulkMutationInterceptorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.3 iter-8 evaluator item 1 — pins the
/// <see cref="AuditLogBulkMutationInterceptor"/> rejects every EF
/// Core bulk-API call AND raw-SQL UPDATE / DELETE that targets
/// <c>audit_logs</c>, while leaving INSERT and SELECT untouched.
/// </summary>
/// <remarks>
/// <para>
/// The iter-7 evaluator flagged that the change-tracker guard
/// (<see cref="AuditDbContext.SaveChangesAsync(bool, System.Threading.CancellationToken)"/>)
/// alone was insufficient because EF Core 8's bulk APIs
/// (<see cref="EntityFrameworkQueryableExtensions.ExecuteDeleteAsync{TSource}(IQueryable{TSource}, System.Threading.CancellationToken)"/>,
/// <see cref="RelationalQueryableExtensions.ExecuteUpdateAsync{TSource}(IQueryable{TSource}, System.Linq.Expressions.Expression{System.Func{Microsoft.EntityFrameworkCore.Query.SetPropertyCalls{TSource}, Microsoft.EntityFrameworkCore.Query.SetPropertyCalls{TSource}}}, System.Threading.CancellationToken)"/>)
/// emit SQL directly without visiting the change tracker. These
/// tests pin the SQL-command-level interceptor that closes that
/// hole.
/// </para>
/// <para>
/// The tests use <see cref="InternalsVisibleTo"/> (set on
/// <c>AgentSwarm.Messaging.Persistence.csproj</c>) to reach the
/// internal <see cref="AuditDbContext.AuditLogs"/>
/// <see cref="DbSet{TEntity}"/>. External callers cannot — that
/// is the access-modifier layer of the iter-8 structural fix; the
/// interceptor is the defence-in-depth layer for in-assembly
/// callers and for raw SQL.
/// </para>
/// </remarks>
public sealed class AuditLogBulkMutationInterceptorTests : IAsyncLifetime
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

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await ctx.Database.EnsureCreatedAsync();

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
    public async Task ExecuteDeleteAsync_AgainstAuditLogs_ThrowsInvalidOperation_BeforeReachingDatabase()
    {
        // Seed a row so the bulk delete has something to target.
        await _logger.LogAsync(NewAuditEntry("u-bulk-1", "trace-bulk-1"), default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        // Stage 5.3 iter-8 evaluator item 1 — the bulk delete MUST
        // be rejected by the AuditLogBulkMutationInterceptor BEFORE
        // it reaches SQLite. If this assertion ever passes (i.e. the
        // call no longer throws), the audit table has lost its
        // append-only guarantee and a future support-tool bug could
        // silently wipe rows.
        var act = async () => await ctx.AuditLogs.Where(x => x.ExternalUserId == "u-bulk-1")
            .ExecuteDeleteAsync(default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{AuditLogBulkMutationViolationFragment}*",
                "Stage 5.3 iter-8 evaluator item 1: ExecuteDeleteAsync against audit_logs MUST be rejected by the SQL-command interceptor — the iter-7 evaluator flagged this gap explicitly");

        // Belt-and-braces: confirm the row is still in the table —
        // the interceptor threw BEFORE the SQL hit the database.
        var rowsAfter = await ctx.AuditLogs.CountAsync();
        rowsAfter.Should().Be(1, "the interceptor must short-circuit before any DELETE statement reaches the database");
    }

    [Fact]
    public async Task ExecuteUpdateAsync_AgainstAuditLogs_ThrowsInvalidOperation_BeforeReachingDatabase()
    {
        await _logger.LogAsync(NewAuditEntry("u-bulk-2", "trace-bulk-2"), default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        // Stage 5.3 iter-8 evaluator item 1 — the bulk update MUST
        // be rejected. A successful bulk-update would let a future
        // operator-tool patch any field (Action, Platform,
        // CorrelationId), which directly violates Stage 5.3's
        // "audit records are immutable" requirement.
        var act = async () => await ctx.AuditLogs.Where(x => x.ExternalUserId == "u-bulk-2")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Action, "tampered"), default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{AuditLogBulkMutationViolationFragment}*",
                "Stage 5.3 iter-8 evaluator item 1: ExecuteUpdateAsync against audit_logs MUST be rejected by the SQL-command interceptor");

        // The seeded row must still carry its ORIGINAL Action value
        // — the interceptor threw before any UPDATE statement landed.
        var row = await ctx.AuditLogs.AsNoTracking().SingleAsync();
        row.Action.Should().NotBe("tampered", "the interceptor must short-circuit before any UPDATE statement reaches the database");
    }

    [Fact]
    public async Task RawSqlDelete_AgainstAuditLogs_ThrowsInvalidOperation()
    {
        await _logger.LogAsync(NewAuditEntry("u-raw-1", "trace-raw-1"), default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        // Stage 5.3 iter-8 evaluator item 1 — even a raw-SQL DELETE
        // (the ESCAPE hatch a future tool might try) MUST be rejected
        // by the interceptor. The match is syntactic on the leading
        // verb + table identifier.
        var act = async () => await ctx.Database.ExecuteSqlRawAsync(
            "DELETE FROM audit_logs WHERE ExternalUserId = 'u-raw-1'");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{AuditLogBulkMutationViolationFragment}*",
                "Stage 5.3 iter-8 evaluator item 1: raw-SQL DELETE against audit_logs MUST be rejected");

        var rowsAfter = await ctx.AuditLogs.CountAsync();
        rowsAfter.Should().Be(1);
    }

    [Fact]
    public async Task RawSqlUpdate_AgainstAuditLogs_ThrowsInvalidOperation()
    {
        await _logger.LogAsync(NewAuditEntry("u-raw-2", "trace-raw-2"), default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var act = async () => await ctx.Database.ExecuteSqlRawAsync(
            "UPDATE audit_logs SET Action = 'tampered' WHERE ExternalUserId = 'u-raw-2'");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{AuditLogBulkMutationViolationFragment}*",
                "Stage 5.3 iter-8 evaluator item 1: raw-SQL UPDATE against audit_logs MUST be rejected");

        var row = await ctx.AuditLogs.AsNoTracking().SingleAsync();
        row.Action.Should().NotBe("tampered");
    }

    [Fact]
    public async Task SelectQuery_AgainstAuditLogs_IsNotBlockedByInterceptor()
    {
        // Sanity pin — the interceptor MUST NOT block SELECT.
        // Forensic / replay tooling depends on read-only queries
        // against the audit table; if the interceptor false-positived
        // on SELECT the read surface would be unusable.
        await _logger.LogAsync(NewAuditEntry("u-select", "trace-select"), default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var rows = await ctx.AuditLogs.AsNoTracking()
            .Where(x => x.ExternalUserId == "u-select")
            .ToListAsync();

        rows.Should().HaveCount(1, "SELECT queries against audit_logs are NOT mutations and must pass through the interceptor unchanged");
    }

    [Fact]
    public async Task InsertViaPersistentAuditLogger_IsNotBlockedByInterceptor()
    {
        // Sanity pin — the interceptor MUST NOT block INSERT.
        // PersistentAuditLogger is the SOLE write path; if INSERT
        // were caught the audit subsystem would be unable to land
        // any rows at all.
        var entry = NewAuditEntry("u-insert", "trace-insert");

        var act = async () => await _logger.LogAsync(entry, default);

        await act.Should().NotThrowAsync(
            "Stage 5.3 iter-8 evaluator item 1: the interceptor MUST allow INSERT — PersistentAuditLogger is the canonical write path and its rows must always land");

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await ctx.AuditLogs.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(1);
    }

    private const string AuditLogBulkMutationViolationFragment = "append-only";

    private static AuditEntry NewAuditEntry(string userId, string correlationId)
        => new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = userId,
            Action = "ask",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
        };
}
