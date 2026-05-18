using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Auth;
using AgentSwarm.Messaging.Telegram.Webhook;
using AgentSwarm.Messaging.Worker;
using AgentSwarm.Messaging.Worker.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
//     DatabaseInitializer hosted service, AND replaces the in-memory
//     stubs with their persistent siblings — including
//     PersistentOperatorRegistry (Stage 3.4) which becomes the
//     IOperatorRegistry backing the IUserAuthorizationService below.
//
//   * AddTelegram wires TelegramOptions (including OperatorBindings,
//     DevOperators, and UserTenantMappings), the bot client, the
//     inbound pipeline, and the Stage 2.2 stubs. UserTenantMappings
//     (architecture.md §7.1 lines 636-650) is the source of truth
//     for /start onboarding consumed by TelegramUserAuthorizationService.
//
//   * The host registers TelegramUserAuthorizationService (Stage 3.4)
//     as the IUserAuthorizationService implementation, superseding
//     the earlier iter-5 ConfiguredOperatorAuthorizationService that
//     read static OperatorBindings from configuration. The new
//     implementation reads from the persistent IOperatorRegistry
//     for Tier 2 runtime authorization (binding lookup on every
//     non-/start command) and from Telegram:UserTenantMappings for
//     Tier 1 /start onboarding (one OperatorBinding row per
//     configured workspace).
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

// =============================================================
// Stage 5.1 — Secret Management Integration.
//
// Azure Key Vault is layered onto the configuration pipeline AFTER
// the defaults established by WebApplication.CreateBuilder (JSON
// files + environment variables + command-line + User Secrets in
// Development) so vault values take precedence over local sources.
// The provider is only added when `KeyVault:Uri` resolves to an
// absolute URI; an unset / blank value leaves the host on
// User-Secrets-and-env-vars only (the local-dev path documented in
// docs/stories/qq-TELEGRAM-MESSENGER-S/dev-setup.md).
//
// `TelegramKeyVaultSecretManager` performs the brief's required
// flat-secret-name → nested-configuration-key mapping
// (TelegramBotToken → Telegram:BotToken) and acts as an allowlist
// so a shared vault cannot silently bleed unrelated secrets into
// the host configuration.
//
// `DefaultAzureCredential` is the standard token chain used by the
// Azure SDKs: it tries (in order) Workload Identity, Managed
// Identity, Visual Studio / VS Code, Azure CLI, and PowerShell so
// the same code path works in AKS, App Service, and on a
// developer's laptop without code changes.
//
// `ReloadInterval` enables the periodic Key Vault refresh required
// by architecture.md §10 (line 1018) and the §11 Security model
// (line 1091): every `Telegram:SecretRefreshIntervalMinutes`
// (default 5) the provider re-fetches secrets from the vault, fires
// IConfiguration's change-token, and `IOptionsMonitor<TelegramOptions>`
// re-binds Telegram:BotToken so rotation takes effect without a
// process restart — per tech-spec.md R-5.
//
// The wiring is registered as an `IHostBuilder.ConfigureAppConfiguration`
// callback on `builder.Host` rather than a synchronous read of
// `builder.Configuration["KeyVault:Uri"]` at top-level Program code.
// `ConfigureAppConfiguration` callbacks fire in registration order
// during `builder.Build()`. In production this is fine because
// `KeyVault:Uri` is supplied by appsettings / environment variables
// / command-line, all of which were attached to the builder's
// configuration BEFORE Program.cs's top-level statements ran. In
// the integration suite, the test fixture's own
// `ConfigureAppConfiguration` callback (which sets in-memory
// `KeyVault:Uri = https://fake-vault…`) is registered AFTER
// Program.cs's callback, so by the time the bootstrap callback
// fires the test override is NOT YET visible. To bridge that gap
// without abandoning the callback shape (and the production
// guarantee that any approved source can supply the URI), the
// bootstrap consults
// `TelegramKeyVaultBootstrap.OverrideKeyVaultUri` — an
// `AsyncLocal<string?>` test seam that mirrors the existing
// `OverrideSecretClientFactory` seam — BEFORE falling back to
// `configuration["KeyVault:Uri"]`. Production code never sets the
// override and so observes identical behaviour to a bare
// configuration read; tests use the seam to drive the brief's
// "Worker starts with Key Vault URI configured" scenario end-to-end
// without spawning the worker out-of-process or fighting the
// callback queue.
//
// `ASP0013` is the .NET 8 analyzer warning that prefers
// `WebApplicationBuilder.Configuration` over
// `builder.Host.ConfigureAppConfiguration`. That suggestion is the
// right default for a synchronous one-shot read, but the
// deferred-callback shape here is what allows the bootstrap to
// observe the FULLY-MERGED configuration at Build time (rather
// than the pre-callback snapshot a synchronous read would see).
// Suppressing ASP0013 here is therefore intentional and surgical —
// limited to this single registration where the deferred-callback
// shape is load-bearing.
// =============================================================
#pragma warning disable ASP0013
builder.Host.ConfigureAppConfiguration((context, config) =>
{
    TelegramKeyVaultBootstrap.TryAddTelegramKeyVault(config, context.Configuration);
});
#pragma warning restore ASP0013

// EF Core + Telegram + webhook receiver (channel, processor, endpoint).
builder.Services.AddMessagingPersistence(builder.Configuration);
builder.Services.AddTelegram(builder.Configuration);
builder.Services.AddTelegramWebhook();

// Stage 6.3: a minimal /healthz endpoint is mapped below so the
// Dockerfile HEALTHCHECK has something to poll. Phase 6 (observability)
// will replace this with a real composite check (Telegram getMe,
// queue depth, dead-letter depth, database) — for now the bare
// AddHealthChecks() registration gives us a 200-OK liveness probe
// without depending on services that don't exist yet.
//
// Stage 4.2 — `dead_letter_messages` depth check chained onto the
// canonical AddHealthChecks() registration so the existing /healthz
// liveness probe upgrades from a static "200 OK" to a live
// "is the operator drowning in dead-letters?" signal. The check
// reads IDeadLetterQueue.CountAsync and reports Unhealthy when the
// count exceeds DeadLetterQueueOptions.UnhealthyThreshold (default
// 100). IDeadLetterQueue is resolved by DI from the persistent
// PersistentDeadLetterQueue registered via AddMessagingPersistence
// above, so by the time the health check runs the EF-backed depth
// is what surfaces.
builder.Services.AddHealthChecks()
    .AddCheck<DeadLetterQueueHealthCheck>(
        DeadLetterQueueHealthCheck.Name,
        tags: new[] { "dead_letter", "outbound" });

// IUserAuthorizationService — iter-5 evaluator item 1 + Stage 3.4
// onboarding. AddTelegram intentionally does NOT register one to
// keep the loud-failure semantic at the library level. The Worker
// registers TelegramUserAuthorizationService (Stage 3.4) via
// TryAddSingleton so any production replacement supplied BEFORE
// this line wins (TryAddSingleton is first-wins). Singleton
// lifetime matches the singleton TelegramUpdatePipeline:
// TelegramUserAuthorizationService is stateless (it only reads
// IOptionsMonitor<TelegramOptions> + delegates to the registry's
// own scope-per-call pattern), so scoping it would create a
// needless captive-dependency conflict with the singleton pipeline.
//
// TelegramUserAuthorizationService supersedes the iter-5
// ConfiguredOperatorAuthorizationService that read static
// OperatorBindings from configuration: it now reads from the
// persistent IOperatorRegistry (PersistentOperatorRegistry from
// AddMessagingPersistence) for Tier 2 runtime authorization, and
// from Telegram:UserTenantMappings configuration for Tier 1
// /start onboarding (per architecture.md §7.1).
builder.Services.TryAddSingleton<IUserAuthorizationService, TelegramUserAuthorizationService>();

// IAlertService — iter-4 evaluator item 6. The Telegram sender's
// dead-letter path (TelegramMessageSender.EmitDeadLetterAlertAsync)
// resolves IAlertService as an optional dependency; without a
// registered concrete the alert path falls back to the sender's own
// LogCritical line and the operator never sees the alert in any
// dedicated alert sink. Register the LoggingAlertService default
// here via TryAddSingleton so a later out-of-band channel
// (Slack / PagerDuty / second-bot) wired in a future stage can
// replace it without touching this file. Singleton lifetime — the
// service has no per-request state; logger injection is the only
// dependency.
builder.Services.TryAddSingleton<IAlertService, LoggingAlertService>();

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

// Stage 4.1 — durable outbox drainer. Spawns
// OutboundQueue:ProcessorConcurrency (default 10) independent worker
// tasks that dequeue from the PersistentOutboundQueue replaced into
// the container by AddMessagingPersistence above, dispatch through
// the IMessageSender registered by AddTelegram, and emit the
// canonical latency histograms
// (telegram.send.first_attempt_latency_ms,
//  telegram.send.all_attempts_latency_ms,
//  telegram.send.queue_dwell_ms) plus the backpressure counter
// telegram.messages.backpressure_dlq via the singleton
// OutboundQueueMetrics. The processor must be registered AFTER
// AddMessagingPersistence (binds OutboundQueueOptions + replaces
// IOutboundQueue with the persistent impl) and AFTER AddTelegram
// (registers IMessageSender → TelegramMessageSender).
//
// Stage 4.2 — explicit factory so the new RetryPolicy options,
// IDeadLetterQueue, and IAlertService are wired into the processor's
// long ctor. Without the factory the DI activator would fall back
// to the legacy 5-arg ctor (Random isn't registered as a DI service)
// which would silently no-op the new dead-letter ledger and alert
// routing.
builder.Services.AddHostedService<OutboundQueueProcessor>(sp =>
    new OutboundQueueProcessor(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<IOutboundQueue>(),
        sp.GetRequiredService<IMessageSender>(),
        sp.GetRequiredService<IOptions<OutboundQueueOptions>>(),
        sp.GetRequiredService<IOptions<RetryPolicy>>(),
        sp.GetRequiredService<IDeadLetterQueue>(),
        sp.GetRequiredService<IAlertService>(),
        sp.GetRequiredService<OutboundQueueMetrics>(),
        sp.GetRequiredService<TimeProvider>(),
        random: null,
        sp.GetRequiredService<ILogger<OutboundQueueProcessor>>()));

var app = builder.Build();

// =============================================================
// Stage 5.1, step 4 — secret-source validation.
//
// Runs BEFORE `app.Run()` so a misconfigured deployment fails
// startup synchronously with a clear, source-by-source diagnostic
// instead of getting deep into hosted-service start and surfacing
// the failure as a generic OptionsValidationException. Logs at
// Warning level when the token is missing (listing every source
// it inspected and why each one did not provide the value) and
// throws an InvalidOperationException to halt startup. The
// existing `TelegramOptionsValidator` ValidateOnStart() hook is
// retained as defence in depth.
// =============================================================
var secretValidatorLogger = app.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("AgentSwarm.Messaging.Worker.SecretManagement");
// Capture KeyVault:Uri AFTER builder.Build() so the validator's
// diagnostic line reports the value that was effectively applied —
// i.e. including overrides supplied by WebApplicationFactory's
// ConfigureAppConfiguration callbacks and any post-Build env/User
// Secrets layers. Reading from `app.Configuration` (the host's
// finalized IConfiguration) is the canonical post-Build read.
var keyVaultUriForDiagnostic = app.Configuration["KeyVault:Uri"];
TelegramSecretSourceValidator.EnsureBotTokenConfigured(
    app.Configuration,
    app.Environment,
    keyVaultUriForDiagnostic,
    secretValidatorLogger);

// Routing + endpoint. UseRouting is required when the host uses the
// minimal-API endpoint conventions; MapTelegramWebhook attaches the
// TelegramWebhookSecretFilter to the route so unauthenticated POSTs
// are rejected with 403 before the controller deserializes the body.
app.UseRouting();
app.MapTelegramWebhook();

// Liveness probe consumed by the Dockerfile HEALTHCHECK and the
// Stage 7.1 integration-test fixture. Kept on the bare
// AddHealthChecks() registration above so the endpoint is always
// reachable even before Phase 6 adds the composite check.
app.MapHealthChecks("/healthz");

app.Run();
