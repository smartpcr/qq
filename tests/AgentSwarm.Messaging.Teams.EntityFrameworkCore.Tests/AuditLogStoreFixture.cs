using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Test fixture that wires <see cref="SqlAuditLogger"/> and
/// <see cref="SqlAuditLogQueryService"/> against an in-memory SQLite database. Installs
/// <c>BEFORE UPDATE</c> / <c>BEFORE DELETE</c> <c>RAISE(ABORT)</c> triggers on the
/// <c>AuditLog</c> table so the SQLite test scenarios exercise the same immutability
/// guarantee that the SQL Server <c>INSTEAD OF UPDATE</c> / <c>INSTEAD OF DELETE</c>
/// migration triggers provide in production.
/// </summary>
/// <remarks>
/// <para>
/// SQLite does not support <c>INSTEAD OF</c> triggers on base tables (only on views).
/// The closest equivalent is a row-level <c>BEFORE UPDATE</c> / <c>BEFORE DELETE</c>
/// trigger that calls <c>SELECT RAISE(ABORT, ...)</c> to abort the statement and
/// rollback the transaction. The triggers below mirror the production SQL Server
/// triggers' contract: any direct <c>UPDATE</c> or <c>DELETE</c> against the
/// <c>AuditLog</c> table aborts and the row is preserved.
/// </para>
/// <para>
/// Triggers must be installed AFTER <c>EnsureCreated()</c> because EF's
/// <c>EnsureCreated</c> reads the model snapshot, not the migration history, so the
/// raw-SQL trigger DDL the migration emits is NOT replayed automatically. The fixture
/// runs the equivalent SQLite DDL through <see cref="DbContext.Database"/> after the
/// tables exist.
/// </para>
/// </remarks>
internal sealed class AuditLogStoreFixture : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<AuditLogDbContext> _options;
    private readonly TestDbContextFactory _factory;

    public AuditLogStoreFixture()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AuditLogDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new AuditLogDbContext(_options))
        {
            ctx.Database.EnsureCreated();

            // SQLite-flavoured immutability triggers — see the type-level remarks for
            // why this is the closest equivalent to the production SQL Server
            // INSTEAD OF UPDATE / INSTEAD OF DELETE triggers installed by the
            // 20260516120058_InitialAuditLog migration.
            ctx.Database.ExecuteSqlRaw(@"
CREATE TRIGGER IF NOT EXISTS TR_AuditLog_NoUpdate
BEFORE UPDATE ON AuditLog
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AuditLog rows are append-only; UPDATE is forbidden (SQLite trigger TR_AuditLog_NoUpdate).');
END;");

            ctx.Database.ExecuteSqlRaw(@"
CREATE TRIGGER IF NOT EXISTS TR_AuditLog_NoDelete
BEFORE DELETE ON AuditLog
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AuditLog rows are append-only; DELETE is forbidden (SQLite trigger TR_AuditLog_NoDelete).');
END;");
        }

        _factory = new TestDbContextFactory(_options);
        Logger = new SqlAuditLogger(_factory);
        QueryService = new SqlAuditLogQueryService(_factory);
    }

    public SqlAuditLogger Logger { get; }

    public SqlAuditLogQueryService QueryService { get; }

    public AuditLogDbContext CreateContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AuditLogDbContext>
    {
        private readonly DbContextOptions<AuditLogDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AuditLogDbContext> options)
            => _options = options;

        public AuditLogDbContext CreateDbContext()
            => new(_options);
    }
}
