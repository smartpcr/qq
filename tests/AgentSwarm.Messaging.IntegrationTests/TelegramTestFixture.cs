// -----------------------------------------------------------------------
// <copyright file="TelegramTestFixture.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using AgentSwarm.Messaging.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace AgentSwarm.Messaging.IntegrationTests;

/// <summary>
/// Stage 7.1 step 2 — boots the real
/// <see cref="AgentSwarm.Messaging.Worker"/> entry point via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, with the Telegram
/// bot client redirected to a <see cref="FakeTelegramApi"/> WireMock
/// instance. Tests that consume this fixture exercise the full
/// Stage 2.3 outbound sender path — rate limiter, MarkdownV2 escaping,
/// long-message split, and 429 retry — against an in-process fake
/// rather than the real <c>api.telegram.org</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why override <see cref="ITelegramBotClient"/> instead of
/// <see cref="HttpClient.BaseAddress"/>.</b> The <c>Telegram.Bot</c>
/// 22.x client constructs request URIs from
/// <see cref="TelegramBotClientOptions.BaseUrl"/> (its own property),
/// not from <see cref="HttpClient.BaseAddress"/>. Re-pointing the
/// named <c>HttpClient</c> would have no effect; replacing the
/// singleton <see cref="ITelegramBotClient"/> with one whose
/// <c>BaseUrl</c> is the WireMock URL is the correct seam.
/// </para>
/// <para>
/// <b>SQLite in shared-cache memory.</b> Mirrors the unit-test
/// approach in <c>WorkerWebHostIntegrationTests</c> — the database
/// schema is created on startup by a small
/// <see cref="IHostedService"/>, so the Stage 2.4 inbound store and
/// the Stage 2.3 sender's downstream Stage 4.1 outbox (once it
/// exists) can read/write without touching the file system. A
/// dedicated <see cref="SqliteConnection"/> keepalive is held for
/// the fixture's lifetime so the shared-cache database survives EF
/// Core's connection pool churn between operations — see the
/// constructor for the full rationale.
/// </para>
/// </remarks>
public sealed class TelegramTestFixture : IDisposable
{
    public const string BotToken = "111111:integration-test-bot-token";
    public const string SecretToken = "integration-test-secret-token-value";

    private readonly WireMockBackedFactory _factory;
    private readonly SqliteConnection _keepAlive;

    public TelegramTestFixture()
    {
        FakeApi = new FakeTelegramApi();

        // SQLite shared-cache in-memory databases are destroyed the
        // moment the LAST connection with that data source name
        // closes; the TestSchemaInitializer's scope is disposed at
        // the end of StartAsync, and EF Core's connection pool may
        // close all idle connections between operations (e.g. under
        // GC pressure or during a test pause). Holding an open
        // keepalive here matches the pattern in
        // PersistentOutboundDeadLetterStoreIntegrationTests and
        // PersistentOutboundMessageIdIndexIntegrationTests and
        // guarantees the schema survives for the lifetime of the
        // fixture, eliminating the "no such table" flake.
        var dbName = $"integration-test-{Guid.NewGuid():N}";
        var connectionString = $"DataSource={dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        _factory = new WireMockBackedFactory(FakeApi.BaseUrl, connectionString);
    }

    /// <summary>
    /// The WireMock-backed fake Telegram API. Tests inject one-shot
    /// stubs through this instance and assert on
    /// <see cref="FakeTelegramApi.SendMessageRequests"/> after the
    /// sender has run.
    /// </summary>
    public FakeTelegramApi FakeApi { get; }

    /// <summary>
    /// The DI root of the booted Worker. Tests resolve the
    /// <see cref="AgentSwarm.Messaging.Abstractions.IMessageSender"/>
    /// from here to exercise the Stage 2.3 outbound path.
    /// </summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>
    /// Creates an HTTP client targeting the Worker's in-process
    /// <see cref="Microsoft.AspNetCore.TestHost"/>. Use this for
    /// <c>/healthz</c> and <c>/api/telegram/webhook</c> hits.
    /// </summary>
    public HttpClient CreateWorkerClient() => _factory.CreateClient();

    public void Dispose()
    {
        _factory.Dispose();
        FakeApi.Dispose();

        // Dispose the keepalive last so the in-memory database is
        // only torn down after the host (and any background services
        // that might still be flushing) has stopped.
        _keepAlive.Dispose();
    }

    private sealed class WireMockBackedFactory : WebApplicationFactory<Program>
    {
        private readonly string _fakeApiBaseUrl;
        private readonly string _connectionString;

        public WireMockBackedFactory(string fakeApiBaseUrl, string connectionString)
        {
            _fakeApiBaseUrl = fakeApiBaseUrl;
            _connectionString = connectionString;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Shared in-memory SQLite — no disk artifacts; schema
                    // created by TestSchemaInitializer below. The
                    // matching keepalive connection is owned by the
                    // outer TelegramTestFixture so the database
                    // survives EF Core connection pool churn.
                    ["ConnectionStrings:MessagingDb"] = _connectionString,
                    ["MessagingDb:UseMigrations"] = "false",
                    ["Telegram:BotToken"] = BotToken,
                    // UsePolling=false + WebhookUrl=null leaves the
                    // TelegramWebhookRegistrationService and polling
                    // service both registered without dispatching real
                    // setWebhook / getUpdates traffic — the registration
                    // service short-circuits when WebhookUrl is empty,
                    // and the polling service is only registered when
                    // UsePolling=true.
                    ["Telegram:WebhookUrl"] = null,
                    ["Telegram:UsePolling"] = "false",
                    ["Telegram:SecretToken"] = SecretToken,
                    // Generous rate limits so concurrent burst tests
                    // don't accidentally throttle themselves.
                    ["Telegram:RateLimits:GlobalPerSecond"] = "1000",
                    ["Telegram:RateLimits:GlobalBurstCapacity"] = "1000",
                    ["Telegram:RateLimits:PerChatPerMinute"] = "10000",
                    ["Telegram:RateLimits:PerChatBurstCapacity"] = "1000",
                    ["InboundRecovery:SweepIntervalSeconds"] = "3600",
                    ["InboundRecovery:MaxRetries"] = "3",
                    ["InboundProcessing:Concurrency"] = "1",
                });
            });

            builder.ConfigureServices(services =>
            {
                // Re-point the Telegram bot client at the WireMock
                // fake. Replace any existing ITelegramBotClient
                // registration with one whose BaseUrl is the fake's
                // URL. The named HttpClient registration from the
                // AddTelegram extension is reused via IHttpClientFactory.
                services.RemoveAll<ITelegramBotClient>();
                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
                    var httpClient = httpClientFactory.CreateClient(
                        AgentSwarm.Messaging.Telegram.TelegramBotClientFactory.HttpClientName);
                    var options = new TelegramBotClientOptions(
                        token: BotToken,
                        baseUrl: _fakeApiBaseUrl,
                        useTestEnvironment: false);
                    return new TelegramBotClient(options, httpClient);
                });

                services.AddHostedService<TestSchemaInitializer>();
            });

            return base.CreateHost(builder);
        }
    }

    private sealed class TestSchemaInitializer : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public TestSchemaInitializer(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.CompletedTask;
    }
}
