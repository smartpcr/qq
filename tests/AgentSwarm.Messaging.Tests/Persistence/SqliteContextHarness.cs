using AgentSwarm.Messaging.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Shared SQLite-backed harness for the Stage 2.2 store tests. Each test
/// instance owns a private in-memory SQLite database via a long-lived
/// connection (the database vanishes when the connection closes) so the
/// schema persists across the <see cref="IDbContextFactory{TContext}"/>
/// instances under test.
/// </summary>
internal sealed class SqliteContextHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteContextHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Factory = new HarnessContextFactory(_connection);
        using var ctx = Factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public IDbContextFactory<MessagingDbContext> Factory { get; }

    public MessagingDbContext NewContext() => Factory.CreateDbContext();

    public void Dispose() => _connection.Dispose();

    private sealed class HarnessContextFactory : IDbContextFactory<MessagingDbContext>
    {
        private readonly SqliteConnection _connection;

        public HarnessContextFactory(SqliteConnection connection) => _connection = connection;

        public MessagingDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MessagingDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new MessagingDbContext(options);
        }
    }
}
