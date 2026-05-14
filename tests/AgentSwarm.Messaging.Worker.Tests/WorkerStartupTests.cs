using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentSwarm.Messaging.Worker.Tests;

public sealed class WorkerStartupTests : IClassFixture<TestWorkerFactory>
{
    private readonly TestWorkerFactory _factory;

    public WorkerStartupTests(TestWorkerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_Endpoint_Returns_200()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Endpoint_Returns_200_Healthy()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_To_ApiMessages_With_Allowed_Tenant_Returns_200()
    {
        using var client = _factory.CreateClient();
        var activity = new
        {
            type = "message",
            id = Guid.NewGuid().ToString(),
            timestamp = DateTimeOffset.UtcNow,
            serviceUrl = "https://smba.trafficmanager.net/amer/",
            channelId = "msteams",
            from = new { id = "user-1", aadObjectId = "user-aad-1" },
            conversation = new
            {
                id = "conv-1",
                tenantId = _factory.TenantId,
            },
            recipient = new { id = "bot-1" },
            text = "ping",
            channelData = new { tenant = new { id = _factory.TenantId } },
        };
        var json = JsonSerializer.Serialize(activity);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/messages", content);

        // CloudAdapter returns 200 once auth + adapter processing succeeds. We swapped the
        // BotFrameworkAuthentication for the anonymous implementation in TestWorkerFactory
        // so this exercises the full pipeline: TenantValidation → RateLimit →
        // controller → CloudAdapter (anonymous auth) → Bot Framework middleware → handler.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_To_ApiMessages_With_Disallowed_Tenant_Returns_403()
    {
        using var client = _factory.CreateClient();
        var activity = new
        {
            type = "message",
            id = Guid.NewGuid().ToString(),
            conversation = new { id = "c", tenantId = "evil-tenant" },
        };
        var json = JsonSerializer.Serialize(activity);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/messages", content);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
