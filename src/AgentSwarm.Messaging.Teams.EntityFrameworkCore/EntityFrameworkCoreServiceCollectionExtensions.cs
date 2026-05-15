using AgentSwarm.Messaging.Abstractions;
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

    /// <summary>
    /// Register the EF Core context factory for <see cref="TeamsLifecycleDbContext"/>
    /// (shared by both Stage 3.3 stores) and the <see cref="SqlAgentQuestionStore"/>
    /// singleton, exposed under <see cref="IAgentQuestionStore"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent — the underlying <c>AddDbContextFactory</c> call is safe to invoke
    /// twice with identical options, and the singleton registration uses
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton(IServiceCollection, Type, Func{IServiceProvider, object})"/>
    /// so an explicit pre-registration of <see cref="IAgentQuestionStore"/> is
    /// preserved. The context factory is shared with
    /// <see cref="AddSqlCardStateStore"/> so a host that calls both helpers gets a single
    /// pooled-context factory rather than two competing ones.
    /// </remarks>
    /// <param name="services">DI container.</param>
    /// <param name="optionsAction">EF context options (provider, connection string).</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSqlAgentQuestionStore(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsAction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsAction);

        services.AddDbContextFactory<TeamsLifecycleDbContext>(optionsAction);
        services.TryAddSingleton<SqlAgentQuestionStore>();
        services.TryAddSingleton<IAgentQuestionStore>(
            sp => sp.GetRequiredService<SqlAgentQuestionStore>());

        return services;
    }

    /// <summary>
    /// Register the EF Core context factory for <see cref="TeamsLifecycleDbContext"/>
    /// (shared by both Stage 3.3 stores) and the <see cref="SqlCardStateStore"/>
    /// singleton, exposed under <see cref="ICardStateStore"/>. Replaces the Stage 2.1
    /// <c>NoOpCardStateStore</c> stub when the host wires this helper.
    /// </summary>
    public static IServiceCollection AddSqlCardStateStore(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsAction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsAction);

        services.AddDbContextFactory<TeamsLifecycleDbContext>(optionsAction);
        services.TryAddSingleton<SqlCardStateStore>();
        services.TryAddSingleton<ICardStateStore>(
            sp => sp.GetRequiredService<SqlCardStateStore>());

        return services;
    }
}
