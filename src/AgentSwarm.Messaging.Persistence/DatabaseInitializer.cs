using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Initializes the messaging database on application startup.
/// Uses EnsureCreated() by default; applies migrations when configured.
/// </summary>
internal sealed class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _useMigrations;

    public DatabaseInitializer(IServiceScopeFactory scopeFactory, bool useMigrations)
    {
        _scopeFactory = scopeFactory;
        _useMigrations = useMigrations;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        if (_useMigrations)
        {
            await context.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await context.Database.EnsureCreatedAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
