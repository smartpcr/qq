using System.Net;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Worker.Tests;

/// <summary>
/// End-to-end pipeline tests for the worker host. Complements the per-middleware unit tests
/// in <c>AgentSwarm.Messaging.Teams.Tests</c> by exercising the wiring assembled in
/// <c>Program.cs</c> (DI lifetimes + ASP.NET Core middleware pipeline order).
/// </summary>
public sealed class MiddlewarePipelineIntegrationTests :
    IClassFixture<RateLimitedAnonymousWebApplicationFactory>
{
    private readonly RateLimitedAnonymousWebApplicationFactory _factory;

    public MiddlewarePipelineIntegrationTests(RateLimitedAnonymousWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void TenantValidationMiddleware_IsRegisteredAsSingleton()
    {
        // Hardening: the host must register TenantValidationMiddleware as a singleton so the
        // IMiddlewareFactory returns the same instance to every request. A transient
        // registration would silently reallocate per-request state (none today, but the
        // contract is enforced here to prevent regressions).
        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<TenantValidationMiddleware>();
        var b = scope2.ServiceProvider.GetRequiredService<TenantValidationMiddleware>();
        var rootA = _factory.Services.GetRequiredService<TenantValidationMiddleware>();

        Assert.Same(a, b);
        Assert.Same(a, rootA);
    }

    [Fact]
    public void RateLimitMiddleware_IsRegisteredAsSingleton()
    {
        // Hardening: RateLimitMiddleware holds the per-tenant sliding-window counter
        // dictionary as instance state. The host MUST register it as a singleton or the
        // counter state would be lost on every request and the quota would be ineffective.
        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<RateLimitMiddleware>();
        var b = scope2.ServiceProvider.GetRequiredService<RateLimitMiddleware>();
        var rootA = _factory.Services.GetRequiredService<RateLimitMiddleware>();

        Assert.Same(a, b);
        Assert.Same(a, rootA);
    }

    [Fact]
    public async Task PostMessages_Returns429_WhenTenantExceedsRateLimit()
    {
        // Full-pipeline integration test: TenantValidationMiddleware (passes — tenant-a is
        // allow-listed) → RateLimitMiddleware (RateLimitPerTenantPerMinute=1) →
        // CloudAdapter (anonymous mode). With a quota of 1 req/min, the first POST for
        // tenant-a consumes the budget and the second POST must short-circuit at the rate
        // limit with HTTP 429 + Retry-After before reaching CloudAdapter.
        var client = _factory.CreateClient();

        const string body = """
            {
              "type": "message",
              "id": "act-rate-1",
              "channelId": "msteams",
              "channelData": { "tenant": { "id": "tenant-a" } },
              "conversation": { "id": "conv-rate-1", "tenantId": "tenant-a" },
              "from": { "id": "user-1", "name": "User" },
              "recipient": { "id": "bot-1", "name": "Bot" },
              "serviceUrl": "https://smba.example/",
              "text": "hello"
            }
            """;

        // First request consumes the single allowed token.
        var first = await client.PostAsync(
            "/api/messages",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);

        // Second request from the same tenant must trip the rate limit at the HTTP layer.
        var second = await client.PostAsync(
            "/api/messages",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.TryGetValues("Retry-After", out var retryValues));
        var retryAfter = retryValues!.Single();
        Assert.True(int.TryParse(retryAfter, out var seconds));
        Assert.InRange(seconds, 1, 60);
    }
}

/// <summary>
/// Worker factory that wires the host in anonymous-auth mode (empty MicrosoftAppId disables
/// JWT validation in <c>ConfigurationBotFrameworkAuthentication</c>) and pins
/// <c>RateLimitPerTenantPerMinute=1</c> so the second inbound request for any allow-listed
/// tenant exercises the HTTP 429 short-circuit. Used by
/// <see cref="MiddlewarePipelineIntegrationTests"/>.
/// </summary>
public sealed class RateLimitedAnonymousWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Teams:MicrosoftAppId"] = string.Empty,
                ["Teams:MicrosoftAppPassword"] = string.Empty,
                ["Teams:MicrosoftAppType"] = string.Empty,
                ["Teams:MicrosoftAppTenantId"] = "tenant-a",
                ["Teams:AllowedTenantIds:0"] = "tenant-a",
                ["Teams:BotEndpoint"] = "https://bot.example/api/messages",
                ["Teams:RateLimitPerTenantPerMinute"] = "1",
                ["Teams:DeduplicationTtlMinutes"] = "10",
                ["Teams:MaxRetryAttempts"] = "5",
                ["Teams:RetryBaseDelaySeconds"] = "2",
            });
        });

        builder.ConfigureServices(services =>
        {
            var toRemove = services
                .Where(d => d.ServiceType == typeof(IValidateOptions<TeamsMessagingOptions>))
                .ToList();
            foreach (var d in toRemove) services.Remove(d);
        });

        return base.CreateHost(builder);
    }
}
