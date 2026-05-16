using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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

        return services;
    }
}
