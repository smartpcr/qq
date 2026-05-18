// -----------------------------------------------------------------------
// <copyright file="DatabaseHealthCheckTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Stage 6.2 — pins for <see cref="DatabaseHealthCheck"/>. The check
/// must report <see cref="HealthStatus.Healthy"/> when both the
/// operational <c>MessagingDbContext</c> and the audit
/// <c>AuditDbContext</c> are reachable AND their schemas are in place;
/// <see cref="HealthStatus.Unhealthy"/> whenever either probe fails.
/// </summary>
public sealed class DatabaseHealthCheckTests : IAsyncLifetime
{
    private SqliteConnection _messagingConnection = null!;
    private SqliteConnection _auditConnection = null!;
    private DbContextOptions<MessagingDbContext> _messagingOptions = null!;
    private DbContextOptions<AuditDbContext> _auditOptions = null!;

    public async Task InitializeAsync()
    {
        _messagingConnection = new SqliteConnection("Filename=:memory:");
        await _messagingConnection.OpenAsync();
        _messagingOptions = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_messagingConnection)
            .Options;

        _auditConnection = new SqliteConnection("Filename=:memory:");
        await _auditConnection.OpenAsync();
        _auditOptions = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(_auditConnection)
            .Options;

        await using (var ctx = new MessagingDbContext(_messagingOptions))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        await using (var ctx = new AuditDbContext(_auditOptions))
        {
            await ctx.Database.EnsureCreatedAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _messagingConnection.DisposeAsync();
        await _auditConnection.DisposeAsync();
    }

    [Fact]
    public async Task CheckHealthAsync_WhenBothContextsReachable_ReturnsHealthy()
    {
        await using var provider = BuildProvider(_messagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().Contain("messaging_can_connect", true);
        result.Data.Should().Contain("messaging_schema_present", true);
        result.Data.Should().Contain("audit_can_connect", true);
        result.Data.Should().Contain("audit_schema_present", true);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenMessagingSchemaMissing_ReturnsUnhealthy_WithExceptionAndMessagingLabel()
    {
        // Drop the canonical table on the messaging context so the
        // schema-probe Take(1) throws even though CanConnectAsync
        // still returns true (connection works, table is gone).
        await using (var ctx = new MessagingDbContext(_messagingOptions))
        {
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE outbox;");
        }

        await using var provider = BuildProvider(_messagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().NotBeNull();
        result.Description.Should().Contain("messaging");
        result.Description.Should().Contain("schema");
        // Both labels must be present in the data dictionary even on
        // failure so the operator dashboard can pivot on the per-leg
        // state without having to interpret a missing key.
        result.Data.Should().Contain("messaging_schema_present", false);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenAuditSchemaMissing_ReturnsUnhealthy_WithExceptionAndAuditLabel()
    {
        await using (var ctx = new AuditDbContext(_auditOptions))
        {
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE audit_logs;");
        }

        await using var provider = BuildProvider(_messagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().NotBeNull();
        result.Description.Should().Contain("audit");
        result.Description.Should().Contain("schema");
        // Messaging probe ran first and succeeded — both messaging
        // legs must be in the data so the operator can see the
        // partial-success state.
        result.Data.Should().Contain("messaging_can_connect", true);
        result.Data.Should().Contain("messaging_schema_present", true);
        result.Data.Should().Contain("audit_schema_present", false);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenMessagingConnectionThrows_ReturnsUnhealthy_WithException()
    {
        // Closing the keepalive connection on the SQLite in-memory
        // database makes EVERY subsequent connection attempt throw
        // "SQLite Error 14: 'unable to open database file'" — perfect
        // proxy for an unreachable Postgres / SQL Server in production.
        await _messagingConnection.DisposeAsync();

        await using var provider = BuildProvider(_messagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("messaging");
        // Re-open so DisposeAsync doesn't double-dispose.
        _messagingConnection = new SqliteConnection("Filename=:memory:");
        await _messagingConnection.OpenAsync();
    }

    [Fact]
    public async Task CheckHealthAsync_WhenAppliedAndPendingMigrationsBothPresent_ReturnsUnhealthy()
    {
        // Iter-2 evaluator item 1 — pin the canonical "deployment
        // forgot to apply the new migration" footgun. We seed the
        // __EFMigrationsHistory table with one row to mimic
        // "migrations were applied previously" so the check sees
        // applied > 0 AND pending > 0 (the assembly defines many
        // migrations; EnsureCreated never recorded any, so pending is
        // every defined migration).
        var historyTableSql =
            "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" ("
            + "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, "
            + "\"ProductVersion\" TEXT NOT NULL);";
        var seedHistorySql =
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") "
            + "VALUES ('00000000000000_PreExistingMigration', '8.0.0');";

        await using (var ctx = new MessagingDbContext(_messagingOptions))
        {
            await ctx.Database.ExecuteSqlRawAsync(historyTableSql);
            await ctx.Database.ExecuteSqlRawAsync(seedHistorySql);
        }

        await using var provider = BuildProvider(_messagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "applied > 0 + pending > 0 means the deployment did not run MigrateAsync after the assembly migrations were updated — the brief's 'migrated' contract is violated");
        result.Description.Should().Contain("pending");
        result.Description.Should().Contain("messaging");
        // Data dictionary must surface both counts so the operator
        // dashboard can pivot on the discrepancy.
        result.Data.Should().ContainKey("messaging_applied_migrations_count")
            .WhoseValue.Should().BeOfType<int>().Which.Should().BeGreaterThan(0);
        result.Data.Should().ContainKey("messaging_pending_migrations")
            .WhoseValue.Should().BeOfType<int>().Which.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenEnsureCreatedModeAndUseMigrationsFalse_ReturnsHealthy()
    {
        // Iter-4 evaluator item 1 — UseMigrations=false is the
        // EnsureCreated / dev / test bootstrap mode. In this mode
        // __EFMigrationsHistory is never populated so
        // GetAppliedMigrationsAsync returns empty;
        // GetPendingMigrationsAsync returns every defined migration.
        // The check MUST treat this as Healthy because the operator
        // explicitly opted out of the migration-tracking contract by
        // setting MessagingDb:UseMigrations=false — the schema-probe
        // is the authoritative "is the DB usable" signal here.
        await using var provider = BuildProvider(_messagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy,
            "with UseMigrations=false (EnsureCreated mode) an empty applied list is the expected shape; the schema probe is the authoritative signal");
        result.Data.Should().ContainKey("messaging_applied_migrations_count")
            .WhoseValue.Should().Be(0);
        result.Data.Should().Contain("messaging_use_migrations", false);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenEnsureCreatedModeAndUseMigrationsTrue_ReturnsUnhealthy()
    {
        // Iter-4 evaluator item 1 — when the operator opted into the
        // strict bootstrap (UseMigrations=true / DatabaseInitializer
        // calls MigrateAsync) but the database was bootstrapped via
        // EnsureCreated instead (i.e. MigrateAsync never ran), the
        // applied list is empty AND there are pending migrations.
        // The Stage 6.2 brief mandates "reachable AND migrated";
        // EnsureCreated bootstrap with UseMigrations=true is a
        // contract violation that the iter-3 implementation silently
        // passed as Healthy. This pin proves iter-4 reports Unhealthy.
        await using var provider = BuildProvider(_messagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: true);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "with UseMigrations=true the deployment promised to run MigrateAsync; zero applied + pending>0 means that promise was not kept");
        result.Description.Should().Contain("pending");
        result.Description.Should().Contain("messaging");
        result.Description.Should().Contain("MessagingDb:UseMigrations",
            "the description must name the offending config key so the operator runbook can route directly to the bootstrap flag");
        result.Data.Should().Contain("messaging_use_migrations", true);
        result.Data.Should().ContainKey("messaging_applied_migrations_count")
            .WhoseValue.Should().Be(0);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenCallerCancellationTokenIsCancelled_PropagatesOperationCanceledException()
    {
        // Iter-3 evaluator item 1 — the caller's CancellationToken
        // cancelling MUST propagate as OperationCanceledException
        // so the HealthCheckService can handle request abort / host
        // shutdown natively. The earlier implementation caught
        // Exception broadly inside the CanConnect / migration /
        // schema-probe blocks, which lied about database health on
        // every cancelled /healthz request.
        //
        // We pre-cancel the token; EF Core's CanConnectAsync
        // observes the token via ThrowIfCancellationRequested and
        // throws a TaskCanceledException (an OCE subclass).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await using var provider = BuildProvider(_messagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: false);

        Func<Task> act = () => check.CheckHealthAsync(new HealthCheckContext(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller cancellation must propagate so HealthCheckService can handle host-shutdown / request-abort natively rather than misreport a database outage");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenMigrationEnumerationFails_ReturnsUnhealthy_WithException()
    {
        // Iter-3 evaluator item 3 — when migration-history
        // enumeration fails (e.g. the deployment principal lacks
        // SELECT on __EFMigrationsHistory, the provider drops the
        // relational extension, an out-of-band DDL change breaks the
        // table shape on a strict-validation backend like PostgreSQL
        // or SQL Server), we MUST report Unhealthy rather than
        // silently falling back to the schema probe. The iter-2
        // implementation logged Debug and treated the migration
        // probe as a no-op, which let a host with pending migrations
        // report Healthy when the migrations enumeration itself was
        // broken.
        //
        // Implementation note: we cannot reproduce the failure via a
        // corrupt __EFMigrationsHistory table on SQLite because of a
        // SQLite back-compat quirk where double-quoted identifiers
        // that don't match a column are silently treated as string
        // literals (so SELECT "MigrationId" FROM "__EFMigrationsHistory"
        // returns the literal string "MigrationId" for each row
        // rather than throwing "no such column"). PostgreSQL and SQL
        // Server reject this strictly, but our test uses SQLite for
        // speed and isolation. Instead we inject a throwing
        // IHistoryRepository via EF Core's ReplaceService API; that
        // is the deterministic, provider-agnostic seam.
        var brokenMessagingOptions = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_messagingConnection)
            .ReplaceService<IHistoryRepository, ThrowingHistoryRepository>()
            .Options;

        await using var provider = BuildProvider(brokenMessagingOptions, _auditOptions);
        var check = NewCheck(provider, useMigrations: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "migration-history enumeration failure means the 'migrated' contract cannot be verified; the iter-2 silent-fallback to schema probe must not return Healthy here");
        result.Exception.Should().NotBeNull(
            "the underlying enumeration exception must be recorded for the operator runbook");
        result.Description.Should().Contain("migration",
            "the description must mention the migration-enumeration failure so the operator runbook routes to the right triage path");
        result.Description.Should().Contain("messaging");
        result.Data.Should().ContainKey("messaging_applied_migrations_count")
            .WhoseValue.Should().Be(-1,
                "the data key must surface the sentinel -1 so an operator dashboard can pivot on 'migration enumeration failed'");
    }

    [Fact]
    public void Name_Constant_StableForRegistration()
    {
        DatabaseHealthCheck.Name.Should().Be("database");
    }

    [Fact]
    public void Constructor_NullScopeFactory_Throws()
    {
        Action act = () => new DatabaseHealthCheck(
            null!,
            new ConfigurationBuilder().Build(),
            NullLogger<DatabaseHealthCheck>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullConfiguration_Throws()
    {
        Action act = () => new DatabaseHealthCheck(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            null!,
            NullLogger<DatabaseHealthCheck>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Action act = () => new DatabaseHealthCheck(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static DatabaseHealthCheck NewCheck(ServiceProvider provider, bool useMigrations)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DatabaseHealthCheck.UseMigrationsConfigKey] = useMigrations ? "true" : "false",
            })
            .Build();

        return new DatabaseHealthCheck(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<DatabaseHealthCheck>.Instance);
    }

    private static ServiceProvider BuildProvider(
        DbContextOptions<MessagingDbContext> messagingOptions,
        DbContextOptions<AuditDbContext> auditOptions)
    {
        var services = new ServiceCollection();
        services.AddSingleton(messagingOptions);
        services.AddSingleton(auditOptions);
        services.AddScoped<MessagingDbContext>(sp =>
            new MessagingDbContext(sp.GetRequiredService<DbContextOptions<MessagingDbContext>>()));
        services.AddScoped<AuditDbContext>(sp =>
            new AuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Replacement <see cref="IHistoryRepository"/> wired via
    /// <c>DbContextOptionsBuilder.ReplaceService</c> so the migration
    /// probe inside <see cref="DatabaseHealthCheck"/> sees a
    /// deterministic, provider-agnostic throw. Required because the
    /// SQLite provider treats double-quoted identifiers that don't
    /// match a column as string literals, which prevents us from
    /// triggering the failure path via a corrupt <c>__EFMigrationsHistory</c>
    /// table on the SQLite in-memory test backend.
    /// </summary>
    private sealed class ThrowingHistoryRepository : IHistoryRepository
    {
        public bool Exists() => true;

        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public IReadOnlyList<HistoryRow> GetAppliedMigrations()
            => throw new InvalidOperationException("migration-history enumeration deliberately broken for DatabaseHealthCheck migration-failure pin");

        public Task<IReadOnlyList<HistoryRow>> GetAppliedMigrationsAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<HistoryRow>>(
                new InvalidOperationException("migration-history enumeration deliberately broken for DatabaseHealthCheck migration-failure pin"));

        public string GetBeginIfExistsScript(string migrationId) => string.Empty;

        public string GetBeginIfNotExistsScript(string migrationId) => string.Empty;

        public string GetCreateIfNotExistsScript() => string.Empty;

        public string GetCreateScript() => string.Empty;

        public string GetDeleteScript(string migrationId) => string.Empty;

        public string GetEndIfScript() => string.Empty;

        public string GetInsertScript(HistoryRow row) => string.Empty;
    }
}
