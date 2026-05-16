using System;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AgentSwarm.Messaging.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Stage 3.4 (iter-5 evaluator item 1) — EF Core provider switch.
    /// The implementation-plan brief mandates "SQLite for dev/local
    /// and PostgreSQL or SQL Server for production" (consistent with
    /// Stages 4.1, 5.3). The provider is selected by the
    /// <c>MessagingDb:Provider</c> configuration value:
    /// <list type="bullet">
    ///   <item><description><c>Sqlite</c> (default) — uses
    ///   <see cref="SqliteDbContextOptionsBuilderExtensions.UseSqlite{TContext}(DbContextOptionsBuilder{TContext}, string, Action{Microsoft.EntityFrameworkCore.Infrastructure.SqliteDbContextOptionsBuilder}?)"/>;
    ///   matches the design-time factory and the only migration set
    ///   committed today.</description></item>
    ///   <item><description><c>PostgreSQL</c> (aliases: <c>Postgres</c>,
    ///   <c>Npgsql</c>) — uses
    ///   <c>Npgsql.EntityFrameworkCore.PostgreSQL.UseNpgsql</c>.
    ///   Production deployments must generate provider-specific
    ///   migrations (see
    ///   <see cref="DesignTimeMessagingDbContextFactory"/>); the
    ///   <c>operator_bindings</c> model is provider-agnostic.</description></item>
    ///   <item><description><c>SqlServer</c> (alias: <c>MSSQL</c>) —
    ///   uses <c>Microsoft.EntityFrameworkCore.SqlServer.UseSqlServer</c>.
    ///   Same migration caveat as PostgreSQL.</description></item>
    /// </list>
    /// Unknown / typo'd provider names throw
    /// <see cref="System.NotSupportedException"/> at host startup so
    /// the operator sees a clear error instead of silently falling
    /// back to a different store.
    /// </summary>
    public static IServiceCollection AddMessagingPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MessagingDb")
            ?? "Data Source=messaging.db";

        var providerName = configuration["MessagingDb:Provider"];
        var provider = ResolveProvider(providerName);

        services.AddDbContext<MessagingDbContext>(options =>
        {
            switch (provider)
            {
                case MessagingDbProvider.Sqlite:
                    options.UseSqlite(connectionString);
                    break;
                case MessagingDbProvider.PostgreSql:
                    options.UseNpgsql(connectionString);
                    break;
                case MessagingDbProvider.SqlServer:
                    options.UseSqlServer(connectionString);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Internal error: ResolveProvider returned unrecognised value {provider}.");
            }
        });

        var useMigrations = configuration.GetValue<bool>("MessagingDb:UseMigrations", false);
        services.AddSingleton<IHostedService>(sp =>
            new DatabaseInitializer(sp.GetRequiredService<IServiceScopeFactory>(), useMigrations));

        // Iter-3 evaluator item 3 — durable Telegram message_id →
        // CorrelationId reverse index. Registered as a singleton
        // because the implementation uses IServiceScopeFactory to
        // create a fresh scope per call (bridging the singleton sender
        // to the scoped DbContext without violating the
        // captive-dependency rule). Replaces the prior best-effort
        // cache-only mapping in TelegramMessageSender.
        services.Replace(ServiceDescriptor.Singleton<IOutboundMessageIdIndex, PersistentOutboundMessageIdIndex>());

        // Iter-4 evaluator item 4 — durable outbound dead-letter
        // ledger. Same singleton + IServiceScopeFactory pattern as
        // the message-id index; replaces the in-memory fallback that
        // AddTelegram registers via TryAddSingleton.
        services.Replace(ServiceDescriptor.Singleton<IOutboundDeadLetterStore, PersistentOutboundDeadLetterStore>());

        // Stage 3.2 — durable task-to-operator oversight assignment
        // repository. Same singleton + IServiceScopeFactory pattern;
        // replaces the in-memory StubTaskOversightRepository that
        // AddTelegram registers via TryAddSingleton.
        services.Replace(ServiceDescriptor.Singleton<ITaskOversightRepository, PersistentTaskOversightRepository>());

        // Stage 3.2 iter-2 evaluator item 5 — durable audit log.
        // Replaces the NullAuditLogger TryAddSingleton fallback in
        // AddTelegram so handoff and human-response audit writes
        // actually persist when persistence is wired. Stage 5.3 will
        // extend the schema with tenant / platform columns; this
        // writer is forward-compatible (additive columns).
        services.Replace(ServiceDescriptor.Singleton<IAuditLogger, PersistentAuditLogger>());

        // Stage 3.4 — durable operator registry. Same singleton +
        // IServiceScopeFactory pattern as the other persistent
        // implementations; replaces the StubOperatorRegistry that
        // AddTelegram registers via TryAddSingleton. The deterministic
        // binding-id derivation in PersistentOperatorRegistry is
        // intentionally distinct from the StubOperatorRegistry
        // derivation so a fixture that flips between the two surfaces
        // can assert the transition explicitly.
        services.Replace(ServiceDescriptor.Singleton<IOperatorRegistry, PersistentOperatorRegistry>());

        return services;
    }

    /// <summary>
    /// Maps the human-friendly provider name from configuration to the
    /// internal <see cref="MessagingDbProvider"/> enum. Accepts common
    /// aliases (<c>Postgres</c>, <c>Npgsql</c>, <c>MSSQL</c>) so the
    /// operator can use whichever spelling matches their environment's
    /// existing connection-string naming convention. Case-insensitive.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="providerName"/> is non-null /
    /// non-empty but does not match any of the supported provider
    /// names. The exception is thrown at
    /// <see cref="AddMessagingPersistence"/> call time (host bootstrap)
    /// so the operator sees the misconfiguration BEFORE any DB
    /// connection is attempted.
    /// </exception>
    internal static MessagingDbProvider ResolveProvider(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return MessagingDbProvider.Sqlite;
        }

        return providerName.Trim().ToLowerInvariant() switch
        {
            "sqlite" => MessagingDbProvider.Sqlite,
            "postgresql" or "postgres" or "npgsql" => MessagingDbProvider.PostgreSql,
            "sqlserver" or "mssql" => MessagingDbProvider.SqlServer,
            _ => throw new NotSupportedException(
                $"MessagingDb:Provider value \"{providerName}\" is not recognised. "
                + "Supported values: \"Sqlite\" (default, dev/local), \"PostgreSQL\" "
                + "(aliases: Postgres, Npgsql), \"SqlServer\" (alias: MSSQL). The "
                + "implementation-plan brief specifies SQLite for dev/local and "
                + "PostgreSQL or SQL Server for production (consistent with Stages 4.1, 5.3)."),
        };
    }
}

/// <summary>
/// Internal enumeration of the EF Core providers the persistence
/// layer wires support for. Kept internal because the provider
/// choice is a host-bootstrap concern; downstream code reads
/// <see cref="MessagingDbContext"/> through <c>IServiceProvider</c>
/// and is provider-agnostic.
/// </summary>
internal enum MessagingDbProvider
{
    Sqlite,
    PostgreSql,
    SqlServer,
}
