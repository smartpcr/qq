using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMessagingPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MessagingDb")
            ?? "Data Source=messaging.db";

        services.AddDbContext<MessagingDbContext>(options =>
            options.UseSqlite(connectionString));

        // Defensive: DatabaseInitializer requires ILogger<T>. AddLogging
        // is idempotent (uses TryAdd internally) so calling it here is
        // safe even when the host has already configured logging.
        services.AddLogging();

        // EF Core migrations are the canonical schema-evolution path —
        // EnsureCreated cannot add new tables to a pre-existing DB and
        // would silently leave the OutboundMessageIdMapping table absent
        // on upgrade. The DatabaseInitializer always applies pending
        // migrations now (no toggle) so deployments cannot accidentally
        // skip schema updates.
        services.AddSingleton<IHostedService>(sp =>
            new DatabaseInitializer(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<DatabaseInitializer>>()));

        // Stage 2.3 — durable (ChatId, TelegramMessageId) → CorrelationId
        // mapping. Registered as a singleton with explicit AddSingleton
        // (not TryAddSingleton) so we OVERRIDE the in-memory tracker
        // that AgentSwarm.Messaging.Telegram registers via
        // TryAddSingleton — the persistent tracker is the production
        // implementation; the in-memory one is the dev/test fallback.
        services.AddSingleton<IMessageIdTracker, PersistentMessageIdTracker>();

        return services;
    }
}

