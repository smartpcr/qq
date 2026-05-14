using System.Net;
using System.Text;
using System.Text.Json;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Worker.Tests;

public sealed class OptionsValidationTests
{
    private sealed class FactoryWithoutAppId : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // MicrosoftAppId intentionally omitted to trigger startup validation.
                    ["TeamsMessaging:MicrosoftAppPassword"] = "pw",
                    ["TeamsMessaging:MicrosoftAppTenantId"] = "tenant",
                    ["TeamsMessaging:BotEndpoint"] = "https://localhost/api/messages",
                    ["TeamsMessaging:AllowedTenantIds:0"] = "tenant",
                });
            });
            builder.ConfigureServices(services =>
            {
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

    [Fact]
    public void Missing_MicrosoftAppId_Throws_OptionsValidationException_On_Resolution()
    {
        using var factory = new FactoryWithoutAppId();
        var ex = Assert.ThrowsAny<Exception>(() => factory.Services.GetRequiredService<IOptions<TeamsMessagingOptions>>().Value);
        // The validator wraps failures in OptionsValidationException — surface either it
        // directly or its message in the inner exception chain.
        var current = ex;
        var found = false;
        while (current is not null)
        {
            if (current is OptionsValidationException || current.Message.Contains("MicrosoftAppId"))
            {
                found = true;
                break;
            }
            current = current.InnerException;
        }
        Assert.True(found, $"Expected OptionsValidationException for missing MicrosoftAppId; got: {ex}");
    }
}
