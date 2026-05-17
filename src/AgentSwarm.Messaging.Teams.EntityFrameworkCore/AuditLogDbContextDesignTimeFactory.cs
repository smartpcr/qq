using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// Design-time factory consumed by the <c>dotnet ef</c> tooling so the
/// <see cref="AuditLogDbContext"/> migration can be generated against a SQL Server
/// provider without requiring a live connection. Production hosts wire the context
/// with their own connection string via the <c>AddSqlAuditLogger</c> helper on
/// <see cref="EntityFrameworkCoreServiceCollectionExtensions"/>.
/// </summary>
public sealed class AuditLogDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<AuditLogDbContext>
{
    /// <inheritdoc />
    public AuditLogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditLogDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=AuditLogDesign;Trusted_Connection=true")
            .Options;

        return new AuditLogDbContext(options);
    }
}
