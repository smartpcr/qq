using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSwarm.Messaging.Worker.Tests;

/// <summary>
/// <see cref="WebApplicationFactory{T}"/> with the Bot Framework JWT validation swapped for
/// the anonymous authentication implementation so integration tests can POST activities to
/// <c>/api/messages</c> without crafting a signed JWT. Tenant validation and rate limiting
/// still run because they are independent HTTP middleware.
/// </summary>
public sealed class TestWorkerFactory : WebApplicationFactory<Program>
{
    public string TenantId { get; } = "11111111-1111-1111-1111-111111111111";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamsMessaging:MicrosoftAppId"] = "00000000-0000-0000-0000-000000000001",
                ["TeamsMessaging:MicrosoftAppPassword"] = "test-password",
                ["TeamsMessaging:MicrosoftAppTenantId"] = TenantId,
                ["TeamsMessaging:BotEndpoint"] = "https://localhost/api/messages",
                ["TeamsMessaging:AllowedTenantIds:0"] = TenantId,
                ["TeamsMessaging:RateLimitPerTenantPerMinute"] = "1000",
                ["TeamsMessaging:DeduplicationTtlMinutes"] = "10",
                ["TeamsMessaging:MaxRetryAttempts"] = "5",
                ["TeamsMessaging:RetryBaseDelaySeconds"] = "2",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Swap BotFrameworkAuthentication for an anonymous implementation so JWT
            // validation does not reject our synthetic activity bodies.
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(BotFrameworkAuthentication))
                {
                    services.RemoveAt(i);
                }
            }
            services.AddSingleton<BotFrameworkAuthentication>(_ => BotFrameworkAuthenticationFactory.Create());
        });
    }
}
