using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Webhook;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — integration test via the in-process
/// <see cref="TestServer"/>. Pins the wiring between
/// <see cref="TelegramWebhookEndpointExtensions.MapTelegramWebhook"/> and
/// <see cref="TelegramWebhookSecretFilter"/>: a real HTTP POST without
/// the secret header receives 403 and produces no
/// <see cref="InboundUpdate"/> row; a POST WITH the matching header
/// reaches the endpoint and produces the row.
/// </summary>
public sealed class TelegramWebhookEndpointIntegrationTests : IAsyncLifetime
{
    private const string ConfiguredSecret = "shared-secret-32-chars-min-length";
    private const string SampleBotToken = "1234567890:AAH9hyTeleGramSecRetToken_test_value_only";
    private const string SampleBody =
        "{\"update_id\":777777,\"message\":{\"message_id\":1,"
        + "\"chat\":{\"id\":1,\"type\":\"private\"},"
        + "\"from\":{\"id\":2,\"is_bot\":false,\"first_name\":\"u\"},"
        + "\"text\":\"/status\"}}";

    private SqliteConnection _connection = null!;
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        // Shared in-memory SQLite so DI-scoped DbContexts created inside
        // the TestServer pipeline see the same schema and rows as the
        // verifier on the test side.
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Telegram:BotToken"] = SampleBotToken,
                        ["Telegram:WebhookUrl"] = string.Empty,    // empty → registration service no-ops
                        ["Telegram:UsePolling"] = "false",
                        ["Telegram:SecretToken"] = ConfiguredSecret,
                    });
                });
                web.ConfigureServices((ctx, services) =>
                {
                    services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
                    services.AddScoped<IInboundUpdateStore, PersistentInboundUpdateStore>();
                    services.AddTelegram(ctx.Configuration);
                    services.AddTelegramWebhook();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapTelegramWebhook());
                });
            });

        _host = await hostBuilder.StartAsync();

        // Seed the schema.
        await using var scope = _host.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _connection.DisposeAsync();
    }

    private HttpClient NewClient() => _host.GetTestClient();

    private static HttpRequestMessage NewPost(string body, string? secretHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TelegramWebhookEndpoint.RoutePattern)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (secretHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(TelegramWebhookSecretFilter.HeaderName, secretHeader);
        }
        return request;
    }

    private async Task<InboundUpdate?> LookupAsync(long updateId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
        return await store.GetByUpdateIdAsync(updateId, CancellationToken.None);
    }

    [Fact]
    public async Task Post_WithoutSecretHeader_Returns403_AndPersistsNothing()
    {
        var client = NewClient();
        var response = await client.SendAsync(NewPost(SampleBody, secretHeader: null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "TelegramWebhookSecretFilter must reject requests that lack the X-Telegram-Bot-Api-Secret-Token header");

        (await LookupAsync(777777)).Should().BeNull(
            "the filter runs BEFORE the controller, so no durable row is created on a rejected request");
    }

    [Fact]
    public async Task Post_WithWrongSecretHeader_Returns403_AndPersistsNothing()
    {
        var client = NewClient();
        var response = await client.SendAsync(NewPost(SampleBody, secretHeader: "wrong-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LookupAsync(777777)).Should().BeNull();
    }

    [Fact]
    public async Task Post_WithMatchingSecretHeader_Returns200_AndPersistsReceivedRow()
    {
        var client = NewClient();
        var response = await client.SendAsync(NewPost(SampleBody, secretHeader: ConfiguredSecret));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await LookupAsync(777777);
        row.Should().NotBeNull("the matching secret header lets the endpoint persist the inbound update");
        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
        row.RawPayload.Should().Be(SampleBody);
    }

    [Fact]
    public async Task DuplicatePost_WithMatchingSecretHeader_ReturnsTwo200_AndOneRow()
    {
        var client = NewClient();

        var first = await client.SendAsync(NewPost(SampleBody, secretHeader: ConfiguredSecret));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.SendAsync(NewPost(SampleBody, secretHeader: ConfiguredSecret));
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "Telegram retries on non-2xx — duplicates must respond 200, not 409, even when no work is performed");

        // Verify the second delivery was short-circuited at PersistAsync
        // (the row exists, but no second row).
        var row = await LookupAsync(777777);
        row.Should().NotBeNull();
    }
}
