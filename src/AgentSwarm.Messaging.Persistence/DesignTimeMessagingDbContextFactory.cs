using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// <see cref="IDesignTimeDbContextFactory{TContext}"/> implementation
/// that lets the <c>dotnet ef migrations</c> tooling construct a
/// <see cref="MessagingDbContext"/> WITHOUT booting the application
/// host. Without this factory, <c>dotnet ef</c> falls back to building
/// the Worker's <c>IHost</c>, which fails when downstream phases
/// register services (e.g. the Stage 2.2 pipeline depends on
/// <c>IUserAuthorizationService</c>, a Phase 4 concern not yet wired
/// into <c>AddTelegram</c>); the factory provides a deterministic
/// design-time construction path keyed only on the EF Core provider.
/// </summary>
/// <remarks>
/// The factory uses the SQLite provider with a placeholder
/// <c>messaging-design.db</c> file because migrations are generated
/// against the model only — no connection is actually opened. This
/// matches the production provider so SQL emitted by
/// <c>migrations add</c> is provider-correct for the dev/local
/// deployment. Production deployments that swap to PostgreSQL or SQL
/// Server should regenerate provider-specific migrations against
/// their target provider; the model-level configuration in
/// <see cref="InboundUpdateConfiguration"/> is provider-agnostic.
/// </remarks>
internal sealed class DesignTimeMessagingDbContextFactory
    : IDesignTimeDbContextFactory<MessagingDbContext>
{
    public MessagingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite("Data Source=messaging-design.db")
            .Options;
        return new MessagingDbContext(options);
    }
}
