using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        return services;
    }
}
