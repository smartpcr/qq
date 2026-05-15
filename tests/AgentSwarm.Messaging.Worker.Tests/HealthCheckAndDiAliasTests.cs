using System.Net;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSwarm.Messaging.Worker.Tests;

/// <summary>
/// Stage 2.1 acceptance tests that pin the literal contracts in <c>Program.cs</c>: the
/// health-check body text and the DI alias between <see cref="ChannelInboundEventPublisher"/>
/// and <see cref="IInboundEventPublisher"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both contracts are easy to break in <c>Program.cs</c> through innocent edits — replacing
/// the explicit <see cref="Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions"/>
/// would silently change body content; registering <see cref="ChannelInboundEventPublisher"/>
/// twice (instead of as an alias) would split producer and consumer onto different channels
/// and break Stage 2.3 integration. Pinning both here ensures regressions surface at test
/// time.
/// </para>
/// </remarks>
public sealed class HealthCheckAndDiAliasTests :
    IClassFixture<TeamsConfiguredWebApplicationFactory>
{
    private readonly TeamsConfiguredWebApplicationFactory _factory;

    public HealthCheckAndDiAliasTests(TeamsConfiguredWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsHealthyBodyText()
    {
        // Stage 2.1 acceptance scenario: "/health returns HTTP 200 with status `Healthy`."
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = (await response.Content.ReadAsStringAsync()).Trim();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task ReadyEndpoint_ReturnsHealthyBodyText()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/ready");
        var body = (await response.Content.ReadAsStringAsync()).Trim();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public void ChannelInboundEventPublisher_AliasedAs_IInboundEventPublisher()
    {
        // Stage 2.3 contract: the producer (registered via the IInboundEventPublisher
        // interface) and the consumer (which Stage 2.3's TeamsMessengerConnector resolves via
        // the concrete ChannelInboundEventPublisher to access the channel reader directly)
        // must observe the same singleton instance — otherwise published events would never
        // reach the consumer.
        var concrete = _factory.Services.GetRequiredService<ChannelInboundEventPublisher>();
        var iface = _factory.Services.GetRequiredService<IInboundEventPublisher>();

        Assert.Same(concrete, iface);
    }

    [Fact]
    public void ChannelInboundEventPublisher_ResolvesAsSingleton()
    {
        // The DI alias must additionally be SCOPED at singleton lifetime so per-request
        // scopes share the same instance. A scoped or transient registration would defeat
        // the alias and starve the consumer of events.
        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<ChannelInboundEventPublisher>();
        var b = scope2.ServiceProvider.GetRequiredService<ChannelInboundEventPublisher>();
        var rootA = _factory.Services.GetRequiredService<ChannelInboundEventPublisher>();

        Assert.Same(a, b);
        Assert.Same(a, rootA);
    }
}
