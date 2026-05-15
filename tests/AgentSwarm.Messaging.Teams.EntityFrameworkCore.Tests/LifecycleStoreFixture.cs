using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Test fixture that wires <see cref="SqlAgentQuestionStore"/> and
/// <see cref="SqlCardStateStore"/> against an in-memory SQLite database. Both stores
/// share a single <see cref="TeamsLifecycleDbContext"/> factory so tests can verify
/// cross-table behaviour (a card-state row referencing an AgentQuestions row, for
/// example) without standing up two databases.
/// </summary>
internal sealed class LifecycleStoreFixture : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<TeamsLifecycleDbContext> _options;
    private readonly TestDbContextFactory _factory;

    public LifecycleStoreFixture(TimeProvider? timeProvider = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<TeamsLifecycleDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new TeamsLifecycleDbContext(_options))
        {
            ctx.Database.EnsureCreated();
        }

        _factory = new TestDbContextFactory(_options);
        QuestionStore = new SqlAgentQuestionStore(_factory, timeProvider ?? TimeProvider.System);
        CardStateStore = new SqlCardStateStore(_factory, timeProvider ?? TimeProvider.System);
    }

    public SqlAgentQuestionStore QuestionStore { get; }

    public SqlCardStateStore CardStateStore { get; }

    public TeamsLifecycleDbContext CreateContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<TeamsLifecycleDbContext>
    {
        private readonly DbContextOptions<TeamsLifecycleDbContext> _options;

        public TestDbContextFactory(DbContextOptions<TeamsLifecycleDbContext> options)
            => _options = options;

        public TeamsLifecycleDbContext CreateDbContext()
            => new(_options);
    }
}
