using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// Design-time factory for <see cref="TeamsOutboxDbContext"/> so the <c>dotnet ef</c>
/// tooling can generate migrations against a SQL Server target without a live
/// connection. Production hosts wire the context via
/// <see cref="EntityFrameworkCoreServiceCollectionExtensions.AddSqlMessageOutbox"/>.
/// </summary>
public sealed class TeamsOutboxDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<TeamsOutboxDbContext>
{
    /// <inheritdoc />
    public TeamsOutboxDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TeamsOutboxDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TeamsOutboxDesign;Trusted_Connection=true")
            .Options;

        return new TeamsOutboxDbContext(options);
    }
}
