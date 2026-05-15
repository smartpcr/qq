using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// DI registration helpers for the Stage 4.1 EF Core conversation reference store.
/// </summary>
/// <remarks>
/// <para>
/// The helpers register both the
/// <see cref="IDbContextFactory{TContext}"/> for
/// <see cref="TeamsConversationReferenceDbContext"/> and the
/// <see cref="SqlConversationReferenceStore"/> singleton, exposing the latter under both
/// <see cref="IConversationReferenceStore"/> and <see cref="IConversationReferenceRouter"/>
/// — so a host calling
/// <c>services.AddSqlConversationReferenceStore(o =&gt; o.UseSqlServer(...))</c> followed by
/// <c>services.AddTeamsMessengerConnector()</c> gets a fully-wired connector with no extra
/// router registration required.
/// </para>
/// <para>
/// All registrations use <c>TryAdd*</c> variants so calling the helper multiple times is
/// idempotent and explicit pre-registrations of any of the affected service types are
/// preserved.
/// </para>
/// </remarks>
public static class EntityFrameworkCoreServiceCollectionExtensions
{
    /// <summary>
    /// Register the EF Core context factory for <see cref="TeamsConversationReferenceDbContext"/>
    /// and the <see cref="SqlConversationReferenceStore"/> singleton (exposed under both
    /// <see cref="IConversationReferenceStore"/> and <see cref="IConversationReferenceRouter"/>).
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="optionsAction">Configures the underlying <see cref="DbContextOptionsBuilder"/> — typically <c>UseSqlServer(connectionString)</c>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSqlConversationReferenceStore(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsAction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsAction);

        services.AddDbContextFactory<TeamsConversationReferenceDbContext>(optionsAction);
        services.TryAddSingleton<SqlConversationReferenceStore>();
        services.TryAddSingleton<IConversationReferenceStore>(
            sp => sp.GetRequiredService<SqlConversationReferenceStore>());
        services.TryAddSingleton<IConversationReferenceRouter>(
            sp => sp.GetRequiredService<SqlConversationReferenceStore>());

        return services;
    }
}
