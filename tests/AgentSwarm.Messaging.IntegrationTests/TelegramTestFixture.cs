// -----------------------------------------------------------------------
// <copyright file="TelegramTestFixture.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Types;

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

    /// <summary>
    /// Default polling timeout for the
    /// <see cref="WaitForMessageSentAsync(long, string, TimeSpan?, CancellationToken)"/>
    /// helper. Generous enough to cover end-to-end async processing
    /// (inbound dispatcher → command handler → outbound queue → sender
    /// → fake API) on a loaded CI runner, while still failing fast
    /// enough that genuinely missed sends surface as deterministic
    /// failures rather than test-host timeouts.
    /// </summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);

    private static int s_nextSyntheticUpdateId = 100_000_000;

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

    /// <summary>
    /// Stage 7.1 step 4 helper — POSTs <paramref name="update"/> to the
    /// Worker's <c>/api/telegram/webhook</c> endpoint exactly the way
    /// real Telegram would, including the
    /// <c>X-Telegram-Bot-Api-Secret-Token</c> header so the
    /// <see cref="AgentSwarm.Messaging.Telegram.Webhook.TelegramWebhookSecretFilter"/>
    /// admits the request. Returns the raw <see cref="HttpResponseMessage"/>
    /// so tests can assert on the synchronous-ACK contract
    /// (200 / 400 / 403) before they wait for downstream async
    /// processing to finish.
    /// </summary>
    /// <param name="update">
    /// The Telegram update to deliver. <see cref="Update.Id"/> MUST be
    /// non-zero — the production endpoint rejects updates with no
    /// usable id, and the durable
    /// <see cref="AgentSwarm.Messaging.Abstractions.InboundUpdate"/>
    /// row's primary key is derived from it. Tests that need
    /// deterministic ids should set <c>Id</c> explicitly; tests that
    /// don't care can leave <c>Id = 0</c> and the helper will inject a
    /// monotonically increasing synthetic id via
    /// <see cref="EnsureUpdateId"/>.
    /// </param>
    /// <param name="correlationId">
    /// Optional value for the <c>X-Correlation-ID</c> header. When
    /// <see langword="null"/> the receiver generates its own trace id
    /// via <see cref="System.Diagnostics.Activity"/>. AC006 — "All
    /// messages have correlation id" — relies on this propagation
    /// path, so tests asserting that an inbound correlation id
    /// survives all the way to a Telegram <c>sendMessage</c> body must
    /// set it explicitly here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="HttpResponseMessage"/> produced by the
    /// in-process <see cref="Microsoft.AspNetCore.TestHost"/> stack.
    /// The caller owns disposal.</returns>
    public async Task<HttpResponseMessage> SimulateWebhookUpdateAsync(
        Update update,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        EnsureUpdateId(update);

        // Use the same JSON shape Telegram emits: the production
        // receiver deserialises via Telegram.Bot's JsonBotAPI.Options
        // (camelCase + the SDK's converter graph). Reaching into
        // Telegram.Bot's internal serialisation options keeps the
        // test wire format aligned with whatever a real Telegram
        // server would send and any future SDK upgrade will surface
        // a deliberate test break rather than silent drift.
        var json = JsonSerializer.Serialize(update, global::Telegram.Bot.JsonBotAPI.Options);

        using var client = CreateWorkerClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            global::AgentSwarm.Messaging.Telegram.Webhook.TelegramWebhookEndpoint.RoutePattern)
        {
            Content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Add(
            global::AgentSwarm.Messaging.Telegram.Webhook.TelegramWebhookSecretFilter.HeaderName,
            SecretToken);
        if (!string.IsNullOrEmpty(correlationId))
        {
            request.Headers.Add(
                global::AgentSwarm.Messaging.Telegram.Webhook.TelegramWebhookEndpoint.CorrelationHeaderName,
                correlationId);
        }

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage 7.1 step 4 helper — wraps <paramref name="callbackQuery"/>
    /// in a synthetic <see cref="Update"/> and dispatches it through
    /// <see cref="SimulateWebhookUpdateAsync(Update, string?, CancellationToken)"/>.
    /// Mirrors what real Telegram does when a user taps an inline
    /// button: it bundles the <see cref="CallbackQuery"/> in a fresh
    /// <see cref="Update"/> with its own <c>Update.Id</c> so the
    /// dedup gate stays meaningful.
    /// </summary>
    /// <param name="callbackQuery">
    /// The callback query to deliver. <see cref="CallbackQuery.Id"/>
    /// (the platform-side ack token) and <see cref="CallbackQuery.Data"/>
    /// (the encoded button value) should be set by the caller — the
    /// Stage 3.3 <c>CallbackQueryHandler</c> reads both.
    /// </param>
    /// <param name="updateId">
    /// Optional explicit <see cref="Update.Id"/>. When
    /// <see langword="null"/> a fresh synthetic id is allocated via
    /// <see cref="EnsureUpdateId"/>. AC004 — "Duplicate webhook
    /// delivery does not execute the same human command twice" — uses
    /// this to send the same id twice.
    /// </param>
    /// <param name="correlationId">See
    /// <see cref="SimulateWebhookUpdateAsync(Update, string?, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<HttpResponseMessage> SimulateCallbackQueryAsync(
        CallbackQuery callbackQuery,
        int? updateId = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbackQuery);

        var update = new Update
        {
            Id = updateId ?? NextSyntheticUpdateId(),
            CallbackQuery = callbackQuery,
        };
        return SimulateWebhookUpdateAsync(update, correlationId, cancellationToken);
    }

    /// <summary>
    /// Stage 7.1 step 4 helper — snapshot assertion that the
    /// <see cref="FakeTelegramApi"/> has observed at least one
    /// <c>sendMessage</c> call to <paramref name="chatId"/> whose
    /// <c>text</c> contains <paramref name="textContains"/> as a
    /// substring. Use this when the outbound side effect happened
    /// inside an awaited call (so the message is on the wire by the
    /// time the test resumes); use
    /// <see cref="WaitForMessageSentAsync(long, string, TimeSpan?, CancellationToken)"/>
    /// when the outbound is driven by background processing.
    /// </summary>
    /// <remarks>
    /// Failure messages include the chat id, the substring searched
    /// for, and the full list of observed sends so the operator can
    /// diagnose a near-miss (wrong chat, MarkdownV2-escaped substring,
    /// wrong tenant) without dropping into a debugger.
    /// </remarks>
    public void AssertMessageSent(long chatId, string textContains)
    {
        ArgumentNullException.ThrowIfNull(textContains);

        var observed = FakeApi.SendMessageRequests;
        var match = observed.Any(r => r.ChatId == chatId && r.Text.Contains(textContains, StringComparison.Ordinal));
        if (!match)
        {
            var summary = observed.Count == 0
                ? "(none)"
                : string.Join("; ", observed.Select(r => $"chat={r.ChatId} text=\"{Truncate(r.Text, 80)}\""));
            throw new Xunit.Sdk.XunitException(
                $"Expected at least one sendMessage with chat_id={chatId} containing substring '{textContains}', but none was observed. Observed sends: {summary}");
        }
    }

    /// <summary>
    /// Stage 7.1 step 4 helper — polls
    /// <see cref="FakeTelegramApi.SendMessageRequests"/> until at
    /// least one entry matches <paramref name="chatId"/> +
    /// <paramref name="textContains"/>, or
    /// <paramref name="timeout"/> elapses. Required for AC tests
    /// where the outbound send is driven by an
    /// <see cref="IHostedService"/> (inbound dispatcher → command
    /// handler → outbound queue → sender) and the test cannot await
    /// the final HTTP hop directly.
    /// </summary>
    /// <param name="chatId">Telegram chat id the message must target.</param>
    /// <param name="textContains">Substring the message body must contain.</param>
    /// <param name="timeout">Maximum wait. Defaults to
    /// <see cref="DefaultWaitTimeout"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Xunit.Sdk.XunitException">No matching message
    /// arrived within the timeout window. Message lists every send
    /// that DID arrive for diagnostic purposes.</exception>
    public async Task WaitForMessageSentAsync(
        long chatId,
        string textContains,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textContains);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultWaitTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            // Honour caller cancellation BEFORE doing any I/O — if the
            // caller has already cancelled (e.g. xUnit's test
            // cancellation token), surface OperationCanceledException
            // directly so the harness reports "cancelled" rather than
            // a misleading "assertion failed" from AssertMessageSent.
            cancellationToken.ThrowIfCancellationRequested();

            if (FakeApi.SendMessageRequests.Any(r =>
                r.ChatId == chatId &&
                r.Text.Contains(textContains, StringComparison.Ordinal)))
            {
                return;
            }

            // Deliberately NOT wrapped in try/catch — if the caller's
            // token cancels mid-delay, the resulting
            // OperationCanceledException must propagate so cancellation
            // semantics match the rest of the codebase
            // (PersistentOutboundQueue, TelegramUpdatePipeline, etc.).
            // Only a NATURAL deadline elapse should fall through to
            // the AssertMessageSent diagnostic below.
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        // Re-use the snapshot helper's diagnostic message so timeouts
        // and synchronous misses produce identical failure surfaces.
        // NB: this path is reached ONLY when the timeout deadline
        // elapsed naturally — caller cancellation propagated out of
        // the loop above as OperationCanceledException.
        AssertMessageSent(chatId, textContains);
    }

    /// <summary>
    /// Allocates a synthetic <see cref="Update.Id"/> guaranteed to be
    /// unique across all <see cref="TelegramTestFixture"/> instances
    /// in the current test-host process. Used by the helpers to
    /// auto-fill <c>Update.Id</c> when callers don't supply one.
    /// </summary>
    public static int NextSyntheticUpdateId() => Interlocked.Increment(ref s_nextSyntheticUpdateId);

    private static void EnsureUpdateId(Update update)
    {
        // Telegram.Bot 22.x exposes Update.Id as an int with default
        // value 0. The production webhook endpoint rejects 0 with a
        // 400, which would mask the real test intent — auto-assign so
        // tests that don't care about the id can pass an "empty"
        // Update without surprise 400s.
        if (update.Id == 0)
        {
            update.Id = NextSyntheticUpdateId();
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

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

                // Stage 7.1 step 2 — the brief explicitly mandates
                // "in-memory queue, in-memory dedup". AddMessagingPersistence
                // (called by Program.cs before us) Replace()'d both
                // IOutboundQueue and IDeduplicationService with the
                // EF/SQLite-backed PersistentOutboundQueue and
                // PersistentDeduplicationService. Even though our
                // SQLite ConnectionString is itself an in-memory DB
                // (DataSource=...;Mode=Memory;Cache=Shared), the
                // SERVICE IMPLEMENTATION is still the "persistent"
                // type — that conflates "in-memory storage" with
                // "in-memory service" and obscures which contract is
                // under test. Force the last-Replace-wins swap to the
                // pure in-process types here so:
                //   1. The fixture matches the Stage 7.1 brief verbatim.
                //   2. Integration tests don't accidentally exercise EF
                //      Core change-tracking semantics when their intent
                //      is to drive the connector's own dedup window.
                //   3. The choice is explicit (and self-documenting) at
                //      the composition root rather than implicit in the
                //      connection string.
                // Both replacements are idempotent: the production
                // path that wires PersistentOutboundQueue /
                // PersistentDeduplicationService still works fine
                // outside this fixture; the swap here only affects the
                // test host.
                services.UseInMemoryOutboundQueue();
                services.Replace(ServiceDescriptor.Singleton<IDeduplicationService, SlidingWindowDeduplicationService>());
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
