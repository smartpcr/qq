using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
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
    /// singleton, exposed under <see cref="IAgentQuestionStore"/>. <b>Replaces</b> any
    /// pre-existing <see cref="IAgentQuestionStore"/> registration (e.g. the Stage 2.1
    /// in-memory or no-op stub) by clearing all prior descriptors before adding the
    /// concrete SQL implementation. This matches the Stage 3.3 implementation-plan
    /// requirement that the production store unconditionally wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The underlying <c>AddDbContextFactory</c> call is safe to invoke twice with
    /// identical options. The context factory is shared with
    /// <see cref="AddSqlCardStateStore"/> so a host that calls both helpers gets a single
    /// pooled-context factory rather than two competing ones.
    /// </para>
    /// <para>
    /// <b>Iter-8 fix:</b> previous iterations used <c>TryAddSingleton</c> which silently
    /// preserved any Stage 2.1 stub registration. The Stage 3.3 acceptance contract is
    /// that wiring <c>AddSqlAgentQuestionStore</c> after the no-op default leaves the
    /// production SQL store wired — so this helper uses
    /// <see cref="ServiceCollectionDescriptorExtensions.RemoveAll{T}(IServiceCollection)"/>
    /// before the final <see cref="ServiceCollectionServiceExtensions.AddSingleton{TService}(IServiceCollection, Func{IServiceProvider, TService})"/>.
    /// </para>
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

        // Unconditionally replace any prior IAgentQuestionStore registration so the
        // SQL store wins regardless of whether the Stage 2.1 stub was wired first.
        services.RemoveAll<IAgentQuestionStore>();
        services.AddSingleton<IAgentQuestionStore>(
            sp => sp.GetRequiredService<SqlAgentQuestionStore>());

        return services;
    }

    /// <summary>
    /// Register the EF Core context factory for <see cref="TeamsLifecycleDbContext"/>
    /// (shared by both Stage 3.3 stores) and the <see cref="SqlCardStateStore"/>
    /// singleton, exposed under <see cref="ICardStateStore"/>. <b>Replaces</b> the
    /// Stage 2.1 <c>NoOpCardStateStore</c> stub (or any other pre-existing registration)
    /// when the host wires this helper.
    /// </summary>
    /// <remarks>
    /// <b>Iter-8 fix:</b> uses
    /// <see cref="ServiceCollectionDescriptorExtensions.RemoveAll{T}(IServiceCollection)"/>
    /// followed by an unconditional <see cref="ServiceCollectionServiceExtensions.AddSingleton{TService}(IServiceCollection, Func{IServiceProvider, TService})"/>
    /// instead of <c>TryAddSingleton</c>, matching the implementation-plan requirement
    /// that the production SQL store replaces the no-op stub.
    /// </remarks>
    public static IServiceCollection AddSqlCardStateStore(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsAction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsAction);

        services.AddDbContextFactory<TeamsLifecycleDbContext>(optionsAction);
        services.TryAddSingleton<SqlCardStateStore>();

        services.RemoveAll<ICardStateStore>();
        services.AddSingleton<ICardStateStore>(
            sp => sp.GetRequiredService<SqlCardStateStore>());

        return services;
    }

    /// <summary>
    /// Apply any pending migrations against the <see cref="TeamsLifecycleDbContext"/>
    /// for the resolved service provider. Production hosts call this in a startup hook
    /// (e.g. <c>app.Services.MigrateTeamsLifecycle()</c>) after building the service
    /// provider. Tests continue to use <c>EnsureCreated()</c> against the fixture
    /// DbContext factory directly.
    /// </summary>
    /// <remarks>
    /// The helper resolves the registered <see cref="IDbContextFactory{TContext}"/>,
    /// opens a scoped context, and invokes
    /// <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.Migrate(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade)"/>
    /// — the canonical EF Core production schema deployment path. This is opt-in (not
    /// auto-wired into <see cref="AddSqlAgentQuestionStore"/> or
    /// <see cref="AddSqlCardStateStore"/>) so the host retains explicit control over
    /// when schema changes apply.
    /// </remarks>
    /// <param name="serviceProvider">A built <see cref="IServiceProvider"/>.</param>
    public static void MigrateTeamsLifecycle(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var factory = serviceProvider.GetRequiredService<IDbContextFactory<TeamsLifecycleDbContext>>();
        using var context = factory.CreateDbContext();
        context.Database.Migrate();
    }

    /// <summary>
    /// Register the EF Core context factory for <see cref="TeamsOutboxDbContext"/>
    /// and the <see cref="SqlMessageOutbox"/> singleton, exposed under
    /// <see cref="IMessageOutbox"/>. <b>Replaces</b> any prior
    /// <see cref="IMessageOutbox"/> registration (e.g. the Stage 2.x no-op stub).
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="optionsAction">Configures the underlying
    /// <see cref="DbContextOptionsBuilder"/> — typically
    /// <c>UseSqlServer(connectionString)</c>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSqlMessageOutbox(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsAction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsAction);

        services.AddDbContextFactory<TeamsOutboxDbContext>(optionsAction);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        // Safe default — overridden when the host also calls AddTeamsOutboxEngine() (the
        // Stage 6.1 helper in AgentSwarm.Messaging.Teams.Outbox), but ensures
        // SqlMessageOutbox resolves even when wired in isolation. SqlMessageOutbox
        // requires non-null OutboxOptions via its constructor null-guard.
        services.TryAddSingleton<OutboxOptions>();
        services.TryAddSingleton<SqlMessageOutbox>();

        services.RemoveAll<IMessageOutbox>();
        services.AddSingleton<IMessageOutbox>(
            sp => sp.GetRequiredService<SqlMessageOutbox>());

        return services;
    }

    /// <summary>
    /// Apply any pending migrations against the <see cref="TeamsOutboxDbContext"/>
    /// for the resolved service provider. Production hosts call this in a startup hook
    /// (e.g. <c>app.Services.MigrateTeamsOutbox()</c>) after building the service
    /// provider.
    /// </summary>
    /// <param name="serviceProvider">A built <see cref="IServiceProvider"/>.</param>
    public static void MigrateTeamsOutbox(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var factory = serviceProvider.GetRequiredService<IDbContextFactory<TeamsOutboxDbContext>>();
        using var context = factory.CreateDbContext();
        context.Database.Migrate();
    }
}
