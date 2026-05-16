using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Auth;
using AgentSwarm.Messaging.Telegram.Webhook;
using AgentSwarm.Messaging.Worker;
using Microsoft.Extensions.DependencyInjection.Extensions;

// =============================================================
// AgentSwarm.Messaging.Worker — production host for the Telegram
// messenger receive path. Iter-5 wires the workstream's
// "Webhook Receiver Endpoint" deliverables together:
//
//   * WebApplication.CreateBuilder gives us routing + the ASP.NET
//     Core hosting model for the synchronous webhook endpoint
//     (POST /api/telegram/webhook), without losing the Generic Host
//     plumbing required by the background services
//     (InboundUpdateDispatcher, InboundUpdateRecoveryStartup,
//     InboundRecoverySweep).
//
//   * AddMessagingPersistence wires MessagingDbContext + the
//     DatabaseInitializer hosted service.
//
//   * AddTelegram wires TelegramOptions (including OperatorBindings),
//     the bot client, the inbound pipeline, and the Stage 2.2 stubs.
//
//   * The host registers ConfiguredOperatorAuthorizationService
//     as the IUserAuthorizationService implementation (iter-5
//     evaluator item 1) — AddTelegram deliberately does not stub
//     authorization, but the Worker MUST supply a binding-aware
//     implementation or dispatcher activation throws.
//
//   * AddTelegramWebhook wires the channel, processor, endpoint,
//     secret filter, and the TelegramWebhookRegistrationService that
//     calls Telegram's setWebhook on host startup.
//
//   * AddScoped<IInboundUpdateStore, PersistentInboundUpdateStore>
//     bridges the EF Core context (scoped) to the abstraction the
//     endpoint + dispatcher consume.
//
//   * The dispatcher, recovery startup, and recovery sweep are
//     registered as hosted services in dependency order:
//     recovery-startup runs FIRST (one-shot Processing→Received
//     reset), then the dispatcher and recovery sweep can begin
//     claiming rows.
//
//   * app.MapTelegramWebhook() binds the route at the production
//     path /api/telegram/webhook with the secret filter attached.
// =============================================================

var builder = WebApplication.CreateBuilder(args);

// EF Core + Telegram + webhook receiver (channel, processor, endpoint).
builder.Services.AddMessagingPersistence(builder.Configuration);
builder.Services.AddTelegram(builder.Configuration);
builder.Services.AddTelegramWebhook();

// IUserAuthorizationService — iter-5 evaluator item 1. AddTelegram
// intentionally does NOT register one to keep the loud-failure
// semantic at the library level. The Worker registers
// ConfiguredOperatorAuthorizationService via TryAddSingleton so any
// production replacement supplied BEFORE this line wins (last-wins
// semantics on TryAddSingleton are first-wins; tests can override
// by registering their own implementation BEFORE WebApplicationFactory
// triggers Program). Singleton lifetime matches the singleton
// TelegramUpdatePipeline: ConfiguredOperatorAuthorizationService is
// stateless (it only reads IOptionsMonitor<TelegramOptions>), so
// scoping it would create a needless captive-dependency conflict
// with the singleton pipeline.
builder.Services.TryAddSingleton<IUserAuthorizationService, ConfiguredOperatorAuthorizationService>();

// Persistent inbound update store. Scoped because it consumes the
// scoped MessagingDbContext.
builder.Services.AddScoped<IInboundUpdateStore, PersistentInboundUpdateStore>();

// Background services: order MATTERS at runtime — IHostedService
// instances start in registration order. Recovery startup runs FIRST
// (one-shot Processing→Received reset BEFORE the dispatcher/sweep
// begin claiming rows); the dispatcher and recovery sweep can then
// start safely.
builder.Services.AddHostedService<InboundUpdateRecoveryStartup>();
builder.Services.AddHostedService<InboundUpdateDispatcher>(sp =>
    InboundUpdateDispatcher.CreateFromConfiguration(sp, builder.Configuration));
builder.Services.AddHostedService<InboundRecoverySweep>(sp =>
{
    var raw = builder.Configuration[InboundRecoverySweep.SweepIntervalKey];
    var intervalSeconds = InboundRecoverySweep.DefaultSweepIntervalSeconds;
    if (!string.IsNullOrWhiteSpace(raw)
        && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedInterval)
        && parsedInterval > 0)
    {
        intervalSeconds = parsedInterval;
    }

    var rawMax = builder.Configuration[InboundRecoverySweep.MaxRetriesKey];
    var maxRetries = InboundRecoverySweep.DefaultMaxRetries;
    if (!string.IsNullOrWhiteSpace(rawMax)
        && int.TryParse(rawMax, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedMax)
        && parsedMax > 0)
    {
        maxRetries = parsedMax;
    }

    // Iter-5 evaluator item 3 — periodic stale-Processing reclaim.
    // The default (30 minutes) is far above the story's 2-second P95
    // SLA, so a healthy long-running handler is never falsely reset.
    // Operators tune this via InboundRecovery:StaleProcessingThresholdSeconds
    // when their workload's healthy duration ceiling differs.
    var rawStale = builder.Configuration[InboundRecoverySweep.StaleProcessingThresholdKey];
    var staleSeconds = InboundRecoverySweep.DefaultStaleProcessingThresholdSeconds;
    if (!string.IsNullOrWhiteSpace(rawStale)
        && int.TryParse(rawStale, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedStale)
        && parsedStale > 0)
    {
        staleSeconds = parsedStale;
    }

    return new InboundRecoverySweep(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<InboundRecoverySweep>>(),
        TimeSpan.FromSeconds(intervalSeconds),
        maxRetries,
        TimeSpan.FromSeconds(staleSeconds));
});

var app = builder.Build();

// Routing + endpoint. UseRouting is required when the host uses the
// minimal-API endpoint conventions; MapTelegramWebhook attaches the
// TelegramWebhookSecretFilter to the route so unauthenticated POSTs
// are rejected with 403 before the controller deserializes the body.
app.UseRouting();
app.MapTelegramWebhook();

app.Run();
