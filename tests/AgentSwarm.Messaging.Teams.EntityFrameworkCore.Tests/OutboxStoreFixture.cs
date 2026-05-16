using System.Data.Common;
using AgentSwarm.Messaging.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Test fixture that wires <see cref="SqlMessageOutbox"/> against an in-memory SQLite
/// database. The connection is held open for the fixture lifetime so the in-memory DB
/// survives between context activations.
/// </summary>
internal sealed class OutboxStoreFixture : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<TeamsOutboxDbContext> _options;
    private readonly TestDbContextFactory _factory;

    public OutboxStoreFixture(OutboxOptions? options = null, TimeProvider? timeProvider = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<TeamsOutboxDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new TeamsOutboxDbContext(_options))
        {
            ctx.Database.EnsureCreated();
        }

        Options = options ?? new OutboxOptions();
        _factory = new TestDbContextFactory(_options);
        Store = new SqlMessageOutbox(_factory, Options, timeProvider ?? TimeProvider.System);
    }

    public OutboxOptions Options { get; }

    public SqlMessageOutbox Store { get; }

    public TeamsOutboxDbContext CreateContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<TeamsOutboxDbContext>
    {
        private readonly DbContextOptions<TeamsOutboxDbContext> _options;

        public TestDbContextFactory(DbContextOptions<TeamsOutboxDbContext> options)
            => _options = options;

        public TeamsOutboxDbContext CreateDbContext() => new(_options);
    }
}
