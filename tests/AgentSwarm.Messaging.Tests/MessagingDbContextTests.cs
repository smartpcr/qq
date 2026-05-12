using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgentSwarm.Messaging.Persistence;

namespace AgentSwarm.Messaging.Tests;

public class MessagingDbContextTests
{
    [Fact]
    public async Task MessagingDbContext_ResolvesFromDI_AndCanConnect()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"messaging_test_{Guid.NewGuid():N}.db");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:MessagingDb"] = $"Data Source={dbPath}"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddMessagingPersistence(configuration);

            var provider = services.BuildServiceProvider();

            // Trigger the startup initialization registered by AddMessagingPersistence
            var hostedService = provider.GetRequiredService<IHostedService>();
            await hostedService.StartAsync(CancellationToken.None);

            var context = provider.GetRequiredService<MessagingDbContext>();

            context.Should().NotBeNull();
            context.Database.CanConnect().Should().BeTrue();

            // Dispose provider to release all SQLite connections before cleanup
            await provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void MessagingDbContext_UsesDefaultConnectionString_WhenNotConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddMessagingPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<MessagingDbContext>();

        context.Should().NotBeNull();
    }
}
