using System.Data.Common;
using AgentSwarm.Messaging.Teams.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Test fixture that wires <see cref="SqlConversationReferenceStore"/> against an
/// in-memory SQLite database using the standard
/// <see cref="IDbContextFactory{TContext}"/> pattern. Each instance owns its own SQLite
/// connection (kept alive for the lifetime of the fixture) and rebuilds the schema via
/// <see cref="DatabaseFacade.EnsureCreated"/> — equivalent to running the EF Core migration
/// against a fresh SQLite database.
/// </summary>
internal sealed class StoreFixture : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<TeamsConversationReferenceDbContext> _options;
    private readonly TestDbContextFactory _factory;

    public StoreFixture(TimeProvider? timeProvider = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<TeamsConversationReferenceDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new TeamsConversationReferenceDbContext(_options))
        {
            ctx.Database.EnsureCreated();
        }

        _factory = new TestDbContextFactory(_options);
        Store = new SqlConversationReferenceStore(_factory, timeProvider);
    }

    public SqlConversationReferenceStore Store { get; }

    public TeamsConversationReferenceDbContext CreateContext()
        => new(_options);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<TeamsConversationReferenceDbContext>
    {
        private readonly DbContextOptions<TeamsConversationReferenceDbContext> _options;

        public TestDbContextFactory(DbContextOptions<TeamsConversationReferenceDbContext> options)
            => _options = options;

        public TeamsConversationReferenceDbContext CreateDbContext()
            => new(_options);
    }
}
