using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// Design-time factory consumed by the <c>dotnet ef</c> tooling so migrations can be
/// generated against a SQL Server provider without requiring a live connection. Production
/// hosts wire the context with their own connection string via
/// <see cref="EntityFrameworkCoreServiceCollectionExtensions.AddSqlConversationReferenceStore"/>.
/// </summary>
public sealed class TeamsConversationReferenceDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<TeamsConversationReferenceDbContext>
{
    /// <inheritdoc />
    public TeamsConversationReferenceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TeamsConversationReferenceDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TeamsMessagingDesign;Trusted_Connection=true")
            .Options;

        return new TeamsConversationReferenceDbContext(options);
    }
}
