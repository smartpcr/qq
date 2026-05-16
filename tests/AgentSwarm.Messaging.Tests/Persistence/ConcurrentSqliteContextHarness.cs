using AgentSwarm.Messaging.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// SQLite-backed test harness that supports true concurrent
/// <see cref="MessagingDbContext"/> access from multiple connections at
/// the same time. The default <see cref="SqliteContextHarness"/> uses a
/// private <c>:memory:</c> database scoped to one long-lived connection,
/// which prevents two contexts from racing the same row. This harness:
/// <list type="bullet">
///   <item><description>Uses a uniquely-named shared-cache in-memory database
///     (<c>Data Source=file:&lt;guid&gt;?mode=memory&amp;cache=shared</c>) so every
///     connection sees the same schema and data.</description></item>
///   <item><description>Sets <c>Default Timeout=30</c> seconds on every
///     connection so concurrent writers wait for the SQLite database-level
///     write lock instead of throwing <c>SQLITE_BUSY</c>.</description></item>
///   <item><description>Holds a keep-alive connection open for the lifetime
///     of the harness so the shared in-memory database is not torn down
///     when transient contexts close their connections.</description></item>
/// </list>
/// With this setup, <see cref="Task.WhenAll{TResult}"/> over multiple
/// <see cref="PersistentOutboundQueue.DequeueAsync"/> calls exercises the
/// conditional <c>UPDATE</c> in <c>TryClaimAsync</c> under true
/// contention: both contexts open simultaneously, both read the same
/// candidate set, both queue an UPDATE statement, SQLite serialises the
/// UPDATEs (the busy timeout guarantees no <c>SQLITE_BUSY</c>), and the
/// optimistic-concurrency predicate guarantees only one observes
/// <c>rowsAffected == 1</c>.
/// </summary>
internal sealed class ConcurrentSqliteContextHarness : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _keepAlive;

    public ConcurrentSqliteContextHarness()
    {
        // Per-instance unique DB name so parallel test classes do not
        // collide on the shared cache.
        var dbName = $"concurrent-test-{Guid.NewGuid():N}";
        _connectionString =
            $"Data Source=file:{dbName}?mode=memory&cache=shared;Default Timeout=30";

        // Keep one connection open for the lifetime of the harness so the
        // shared in-memory database persists across transient context
        // connections. Without this, the DB is destroyed as soon as the
        // last connection closes.
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        Factory = new HarnessContextFactory(_connectionString);
        using var ctx = Factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public IDbContextFactory<MessagingDbContext> Factory { get; }

    public MessagingDbContext NewContext() => Factory.CreateDbContext();

    public void Dispose()
    {
        _keepAlive.Dispose();
        // The keep-alive close drops the shared-cache database; pool
        // entries for the same connection string become inert.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    private sealed class HarnessContextFactory : IDbContextFactory<MessagingDbContext>
    {
        private readonly string _connectionString;

        public HarnessContextFactory(string connectionString)
            => _connectionString = connectionString;

        public MessagingDbContext CreateDbContext()
        {
            // Each context opens (or pool-leases) its own connection.
            // This is the topology that production deployment uses and
            // the one that makes the conditional UPDATE in TryClaimAsync
            // observable under true concurrency.
            var options = new DbContextOptionsBuilder<MessagingDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new MessagingDbContext(options);
        }
    }
}
