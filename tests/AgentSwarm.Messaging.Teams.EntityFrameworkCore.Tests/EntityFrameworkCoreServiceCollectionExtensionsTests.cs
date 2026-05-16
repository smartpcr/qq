using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Tests for <see cref="EntityFrameworkCoreServiceCollectionExtensions"/>: verify the helper
/// exposes the SQL store under both <see cref="IConversationReferenceStore"/> and
/// <see cref="IConversationReferenceRouter"/>, satisfying the cast adapter that
/// <c>TeamsServiceCollectionExtensions.AddTeamsMessengerConnector</c> falls back to.
/// </summary>
public class EntityFrameworkCoreServiceCollectionExtensionsTests
{
    [Fact(DisplayName = "AddSqlConversationReferenceStore exposes both store and router from the same singleton")]
    public void AddSqlConversationReferenceStore_RegistersBothInterfaces()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddSqlConversationReferenceStore(o => o.UseSqlite(connection));

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IConversationReferenceStore>();
        var router = provider.GetRequiredService<IConversationReferenceRouter>();

        Assert.IsType<SqlConversationReferenceStore>(store);
        Assert.IsType<SqlConversationReferenceStore>(router);
        Assert.Same(store, router);
    }

    /// <summary>
    /// Iter-8 fix #1 — Stage 3.3 ships an EF Core migration for
    /// <see cref="TeamsLifecycleDbContext"/> (alongside the pre-existing one for the
    /// conversation-reference context). Verify the migration assembly actually contains
    /// the expected migration ID so production hosts can deploy schema via
    /// <c>Database.Migrate()</c> rather than relying on <c>EnsureCreated()</c>.
    /// </summary>
    [Fact]
    public void TeamsLifecycleDbContext_HasInitialMigration()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddSqlAgentQuestionStore(o => o.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TeamsLifecycleDbContext>>();
        using var db = factory.CreateDbContext();

        var migrations = db.Database.GetMigrations().ToList();
        Assert.Contains(migrations, m => m.EndsWith("_InitialTeamsLifecycle", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "AddSqlConversationReferenceStore is idempotent and preserves explicit overrides")]
    public void AddSqlConversationReferenceStore_IsIdempotent()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddSqlConversationReferenceStore(o => o.UseSqlite(connection));
        services.AddSqlConversationReferenceStore(o => o.UseSqlite(connection));

        var storeRegistrations = 0;
        var routerRegistrations = 0;
        foreach (var d in services)
        {
            if (d.ServiceType == typeof(IConversationReferenceStore)) storeRegistrations++;
            if (d.ServiceType == typeof(IConversationReferenceRouter)) routerRegistrations++;
        }

        Assert.Equal(1, storeRegistrations);
        Assert.Equal(1, routerRegistrations);
    }

    /// <summary>
    /// Iter-8 fix #2 — Stage 2.1 typically pre-registers a no-op <c>ICardStateStore</c>
    /// stub. <see cref="EntityFrameworkCoreServiceCollectionExtensions.AddSqlCardStateStore"/>
    /// must unconditionally replace it with the SQL store.
    /// </summary>
    [Fact]
    public void AddSqlCardStateStore_ReplacesPreRegisteredStub_WithSqlStore()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        // Simulate Stage 2.1 stub registration BEFORE the SQL helper.
        services.AddSingleton<AgentSwarm.Messaging.Teams.ICardStateStore, StubCardStateStore>();
        services.AddSqlCardStateStore(o => o.UseSqlite(connection));

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<AgentSwarm.Messaging.Teams.ICardStateStore>();
        Assert.IsType<SqlCardStateStore>(resolved);

        var all = provider.GetServices<AgentSwarm.Messaging.Teams.ICardStateStore>().ToList();
        Assert.Single(all);
        Assert.IsType<SqlCardStateStore>(all[0]);
    }

    /// <summary>
    /// Iter-8 fix #2 — same replacement semantics for the agent-question store. A
    /// pre-registered <see cref="AgentSwarm.Messaging.Abstractions.IAgentQuestionStore"/>
    /// must be unconditionally replaced by the SQL implementation.
    /// </summary>
    [Fact]
    public void AddSqlAgentQuestionStore_ReplacesPreRegisteredStub_WithSqlStore()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton<AgentSwarm.Messaging.Abstractions.IAgentQuestionStore, StubAgentQuestionStore>();
        services.AddSqlAgentQuestionStore(o => o.UseSqlite(connection));

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<AgentSwarm.Messaging.Abstractions.IAgentQuestionStore>();
        Assert.IsType<SqlAgentQuestionStore>(resolved);

        var all = provider.GetServices<AgentSwarm.Messaging.Abstractions.IAgentQuestionStore>().ToList();
        Assert.Single(all);
        Assert.IsType<SqlAgentQuestionStore>(all[0]);
    }

    private sealed class StubCardStateStore : AgentSwarm.Messaging.Teams.ICardStateStore
    {
        public Task SaveAsync(AgentSwarm.Messaging.Teams.TeamsCardState state, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentSwarm.Messaging.Teams.TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
            => Task.FromResult<AgentSwarm.Messaging.Teams.TeamsCardState?>(null);
        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubAgentQuestionStore : AgentSwarm.Messaging.Abstractions.IAgentQuestionStore
    {
        public Task SaveAsync(AgentSwarm.Messaging.Abstractions.AgentQuestion question, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentSwarm.Messaging.Abstractions.AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
            => Task.FromResult<AgentSwarm.Messaging.Abstractions.AgentQuestion?>(null);
        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct)
            => Task.FromResult(false);
        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentSwarm.Messaging.Abstractions.AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<AgentSwarm.Messaging.Abstractions.AgentQuestion?>(null);
        public Task<IReadOnlyList<AgentSwarm.Messaging.Abstractions.AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentSwarm.Messaging.Abstractions.AgentQuestion>>(Array.Empty<AgentSwarm.Messaging.Abstractions.AgentQuestion>());
        public Task<IReadOnlyList<AgentSwarm.Messaging.Abstractions.AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentSwarm.Messaging.Abstractions.AgentQuestion>>(Array.Empty<AgentSwarm.Messaging.Abstractions.AgentQuestion>());
    }
}
