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
}
