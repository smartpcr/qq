using System.Net;
using System.Net.Http.Headers;
using AgentSwarm.Messaging.Teams;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Worker.Tests;

/// <summary>
/// Integration tests for <c>AgentSwarm.Messaging.Worker</c> exercising the full ASP.NET Core
/// pipeline (TenantValidationMiddleware → RateLimitMiddleware → controller / health checks).
/// </summary>
public sealed class WorkerStartupTests :
    IClassFixture<TeamsConfiguredWebApplicationFactory>,
    IClassFixture<AnonymousAuthWebApplicationFactory>
{
    private readonly TeamsConfiguredWebApplicationFactory _factory;
    private readonly AnonymousAuthWebApplicationFactory _anonymousFactory;

    public WorkerStartupTests(
        TeamsConfiguredWebApplicationFactory factory,
        AnonymousAuthWebApplicationFactory anonymousFactory)
    {
        _factory = factory;
        _anonymousFactory = anonymousFactory;
    }

    [Fact]
    public async Task HealthEndpoint_Returns200_OnConfiguredHost()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadyEndpoint_Returns200_OnConfiguredHost()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TenantValidation_Returns403_ForDisallowedTenant()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            "{\"channelData\":{\"tenant\":{\"id\":\"tenant-evil\"}},\"type\":\"message\"}",
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/api/messages", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantValidation_Returns403_WhenTenantMissing()
    {
        var client = _factory.CreateClient();
        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/messages", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostMessages_Returns200_ForValidActivityFromAllowedTenant()
    {
        // Stage 2.1 acceptance scenario: a valid Bot Framework activity from an allowed
        // tenant flows TenantValidationMiddleware → RateLimitMiddleware → TeamsWebhookController
        // → CloudAdapter → TelemetryMiddleware → ActivityDeduplicationMiddleware →
        // TeamsSwarmActivityHandler and yields HTTP 200. The AnonymousAuthWebApplicationFactory
        // disables BotFramework JWT validation (MicrosoftAppId="") so the test exercises the
        // full pipeline without requiring a real Azure Bot registration.
        var client = _anonymousFactory.CreateClient();
        const string body = """
            {
              "type": "message",
              "id": "act-happy-1",
              "channelId": "msteams",
              "channelData": { "tenant": { "id": "tenant-a" } },
              "conversation": { "id": "conv-happy-1", "tenantId": "tenant-a" },
              "from": { "id": "user-1", "name": "User" },
              "recipient": { "id": "bot-1", "name": "Bot" },
              "serviceUrl": "https://smba.example/",
              "text": "agent status"
            }
            """;
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/messages", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// Worker factory that supplies a complete <c>Teams</c> options block so the host's
/// <c>OptionsBuilder.ValidateOnStart()</c> passes during integration testing.
/// </summary>
public sealed class TeamsConfiguredWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Teams:MicrosoftAppId"] = "test-app-id",
                ["Teams:MicrosoftAppPassword"] = "test-password",
                ["Teams:MicrosoftAppType"] = "MultiTenant",
                ["Teams:MicrosoftAppTenantId"] = "tenant-a",
                ["Teams:AllowedTenantIds:0"] = "tenant-a",
                ["Teams:BotEndpoint"] = "https://bot.example/api/messages",
                ["Teams:RateLimitPerTenantPerMinute"] = "100",
                ["Teams:DeduplicationTtlMinutes"] = "10",
                ["Teams:MaxRetryAttempts"] = "5",
                ["Teams:RetryBaseDelaySeconds"] = "2",
            });
        });

        return base.CreateHost(builder);
    }
}

/// <summary>
/// Worker factory configured with an empty <c>MicrosoftAppId</c>, which is the Bot Framework
/// SDK's documented way to disable JWT validation in <see cref="Microsoft.Bot.Connector.Authentication.ConfigurationBotFrameworkAuthentication"/>
/// (anonymous/local-emulator mode). The startup validator
/// (<see cref="TeamsMessagingOptionsValidator"/>) is replaced with a permissive no-op so the
/// host accepts the empty AppId for the duration of the test. Used by the happy-path
/// <c>POST /api/messages</c> scenario where the request must reach <c>CloudAdapter</c>.
/// </summary>
public sealed class AnonymousAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Empty MicrosoftAppId / Password / Type triggers anonymous auth in
                // ConfigurationBotFrameworkAuthentication. Tenant validation still runs
                // because TenantValidationMiddleware enforces AllowedTenantIds at the
                // HTTP layer before the Bot Framework auth pipeline.
                ["Teams:MicrosoftAppId"] = string.Empty,
                ["Teams:MicrosoftAppPassword"] = string.Empty,
                ["Teams:MicrosoftAppType"] = string.Empty,
                ["Teams:MicrosoftAppTenantId"] = "tenant-a",
                ["Teams:AllowedTenantIds:0"] = "tenant-a",
                ["Teams:BotEndpoint"] = "https://bot.example/api/messages",
                ["Teams:RateLimitPerTenantPerMinute"] = "100",
                ["Teams:DeduplicationTtlMinutes"] = "10",
                ["Teams:MaxRetryAttempts"] = "5",
                ["Teams:RetryBaseDelaySeconds"] = "2",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Production validator rejects empty MicrosoftAppId/Password — remove it so the
            // host starts in anonymous test mode. Other validation tests still cover the
            // strict path because they use TeamsConfiguredWebApplicationFactory.
            var toRemove = services
                .Where(d => d.ServiceType == typeof(IValidateOptions<TeamsMessagingOptions>))
                .ToList();
            foreach (var d in toRemove) services.Remove(d);
        });

        return base.CreateHost(builder);
    }
}
