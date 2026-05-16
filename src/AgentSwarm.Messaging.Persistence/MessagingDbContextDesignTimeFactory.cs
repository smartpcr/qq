using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> tooling to instantiate
/// <see cref="MessagingDbContext"/> without spinning up the Worker host.
/// Pinned to SQLite because every relational migration in this solution is
/// generated and applied against the SQLite provider (architecture.md Section
/// 6 "Persistence" pins the store to SQLite for the durable inbox/outbox).
/// </summary>
/// <remarks>
/// The data source is intentionally a placeholder file in the working
/// directory. Migration generation does not connect, but the SQLite provider
/// requires a syntactically valid connection string when building options.
/// Runtime registration in the host (Stage 4.5) supplies the real
/// configuration via <c>AddMessagingPersistence</c>.
/// </remarks>
public class MessagingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<MessagingDbContext>
{
    /// <inheritdoc />
    public MessagingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MessagingDbContext>();
        optionsBuilder.UseSqlite("Data Source=messaging-design-time.db");
        return new MessagingDbContext(optionsBuilder.Options);
    }
}
