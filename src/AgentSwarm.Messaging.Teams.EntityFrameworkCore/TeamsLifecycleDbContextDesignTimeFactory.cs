using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// Design-time factory consumed by the <c>dotnet ef</c> tooling so migrations can be
/// generated against a SQL Server provider without requiring a live connection. Production
/// hosts wire the context with their own connection string via
/// <see cref="EntityFrameworkCoreServiceCollectionExtensions.AddSqlAgentQuestionStore"/> /
/// <see cref="EntityFrameworkCoreServiceCollectionExtensions.AddSqlCardStateStore"/>.
/// </summary>
public sealed class TeamsLifecycleDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<TeamsLifecycleDbContext>
{
    /// <inheritdoc />
    public TeamsLifecycleDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TeamsLifecycleDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TeamsLifecycleDesign;Trusted_Connection=true")
            .Options;

        return new TeamsLifecycleDbContext(options);
    }
}
