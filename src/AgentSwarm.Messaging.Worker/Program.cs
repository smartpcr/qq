using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AgentSwarm.Messaging.Worker;

using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Diagnostics;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Entry point for the AgentSwarm messaging gateway host. The host owns the
/// HTTP surface used by Slack (and other connector) inbound endpoints and
/// runs the background message-processing pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Stage 8.1 (workstream:
/// <c>ws-qq-slack-messenger-supp-phase-connector-wiring-and-acceptance-validation-stage-slackconnector-facade-and-di-wiring</c>):
/// the composition root collapses every per-stage <c>AddSlack*</c> call into
/// the single <see cref="SlackMessengerServiceCollectionExtensions.AddSlackMessenger(IServiceCollection, IConfiguration)"/>
/// facade. The cross-platform primitives are now requested through
/// <see cref="MessagingCoreServiceCollectionExtensions.AddMessagingCore"/> and
/// <see cref="MessagingPersistenceServiceCollectionExtensions.AddMessagingPersistence"/>
/// so future connector additions (Telegram, Discord, Teams) inherit the same
/// host shape without per-connector edits here. The HTTP pipeline calls
/// (<see cref="SlackSignatureValidator"/> middleware, controllers, health
/// endpoints, EnsureCreated bootstrap, production durability guard) remain
/// here because they require <see cref="WebApplication"/>.
/// </para>
/// <para>
/// The host -- not the facade -- owns the SQLite-provider
/// <see cref="DbContextOptionsBuilder.UseSqlite"/> call so the Slack
/// assembly stays decoupled from any single EF provider. A future host
/// that targets PostgreSQL or SQL Server swaps only the registration
/// here; <see cref="SlackMessengerServiceCollectionExtensions.AddSlackMessenger(IServiceCollection, IConfiguration)"/>
/// is unchanged.
/// </para>
/// </remarks>
public class Program
{
    /// <summary>
    /// Configuration key for the SQLite (or other relational) connection
    /// string backing <see cref="SlackPersistenceDbContext"/>.
    /// </summary>
    public const string SlackAuditConnectionStringKey = "SlackAudit";

    /// <summary>
    /// Default connection string used when
    /// <c>ConnectionStrings:<see cref="SlackAuditConnectionStringKey"/></c>
    /// is not configured. Points at a relative SQLite file so a
    /// freshly-cloned host still boots; production deployments
    /// override this via configuration.
    /// </summary>
    private const string DefaultSlackAuditConnectionString =
        "Data Source=slack-audit.db";

    /// <summary>
    /// Configuration key (boolean) that gates the opt-in for the
    /// no-op Slack handler stand-ins
    /// (<see cref="SlackInboundIngestorServiceCollectionExtensions.AddSlackInboundDevelopmentHandlerStubs"/>).
    /// When unset, defaults to
    /// <see cref="HostEnvironmentEnvExtensions.IsDevelopment(Microsoft.Extensions.Hosting.IHostEnvironment)"/>:
    /// Development hosts wire the stubs so the ingestor pipeline
    /// resolves on a dev laptop; Production / Staging / Testing
    /// hosts surface a fail-loud
    /// <see cref="InvalidOperationException"/> from the pipeline ctor
    /// the first time the ingestor lazily resolves it (the ingestor
    /// then forwards the envelope to the last-resort
    /// <see cref="ISlackInboundEnqueueDeadLetterSink"/> instead of
    /// silently ack-and-dropping it). Operators can explicitly opt
    /// in (<c>true</c>) or out (<c>false</c>) per environment.
    /// </summary>
    public const string EnableDevelopmentHandlerStubsKey =
        "Slack:Inbound:EnableDevelopmentHandlerStubs";

    public static void Main(string[] args)
    {
        WebApplication app = BuildApp(args);
        app.Run();
    }

    /// <summary>
    /// Builds the fully-configured <see cref="WebApplication"/> with all
    /// services registered, the EF audit schema provisioned, and the
    /// HTTP pipeline (signature middleware + endpoints) mounted. Returns
    /// the ready-to-run app so both <see cref="Main"/> and integration
    /// tests (<c>WebApplicationFactory&lt;Program&gt;</c>) can drive it
    /// from the same composition root.
    /// </summary>
    public static WebApplication BuildApp(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // MVC routing is host-level, not connector-level.
        builder.Services.AddRouting();

        // Stage 8.1: cross-platform primitives provided by the Core and
        // Persistence projects. AddMessagingCore registers TimeProvider.System
        // (TryAdd, so a test composition root can pre-register a fake clock)
        // and reserves the Messaging:Core options section. AddMessagingPersistence
        // is currently a no-op shim reserved for the upstream Persistence
        // story's cross-platform DbContext + entity configurations; the
        // Slack-specific SlackPersistenceDbContext is owned by this host
        // below (so the Slack assembly stays decoupled from any specific
        // EF provider).
        builder.Services.AddMessagingCore(builder.Configuration);
        builder.Services.AddMessagingPersistence(builder.Configuration);

        // Stage 8.1: secret-provider chain. Registered BEFORE
        // AddSlackMessenger so the AddSlackSignatureValidation call inside
        // the facade (which TryAddSingletons an InMemorySecretProvider
        // fallback) does not win over the composite provider configured
        // via SecretProvider:ProviderType in appsettings.
        builder.Services.AddSecretProvider(builder.Configuration);

        // Stage 3.1: register the durable Slack audit DbContext BEFORE
        // AddSlackMessenger so the facade's EF-bound registrations
        // (audit writer, workspace store, idempotency store, thread
        // mapping lookup) resolve the SQLite-backed context. Resolve
        // the connection string LAZILY via the runtime IServiceProvider
        // so any IConfiguration overrides applied by a
        // WebApplicationFactory<Program> hook (e.g., the integration
        // tests' per-test isolated SQLite path) are honoured -- a
        // builder-time read of builder.Configuration would lock in the
        // appsettings.json default before the hook runs.
        builder.Services.AddDbContext<SlackPersistenceDbContext>((sp, opts) =>
        {
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            string? connectionString = cfg.GetConnectionString(SlackAuditConnectionStringKey);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = DefaultSlackAuditConnectionString;
            }

            opts.UseSqlite(connectionString);
        });

        // Stage 8.1: the single Slack connector facade. Composes every
        // internal Slack registration -- options, persistence (EF audit
        // writer / workspace store / seeder / idempotency / thread
        // mapping), signature validation, authorization, inbound
        // transport + ingestor, command / app-mention / interaction
        // dispatchers, thread lifecycle, outbound dispatcher +
        // SlackConnector, file-system queues + DLQs, health checks,
        // startup diagnostics, telemetry, and the inbound MessengerEvent
        // buffer SlackConnector.ReceiveAsync drains. The host MUST
        // register SlackPersistenceDbContext (above) before this call.
        //
        // The Slack facade does NOT install a default NoOpAgentTaskService
        // stub (silent /agent ask acknowledgement is a footgun). The
        // Worker is still in pre-orchestrator-client mode, so explicitly
        // opt in to the dev stub here; the call is observable in the
        // composition root, and a future commit that wires the real
        // orchestrator simply deletes this single line. Production hosts
        // that register a real orchestrator client BEFORE this call get
        // TryAdd semantics (their real registration wins).
        builder.Services.AddSlackCommandDispatcherDevelopmentDefaults();

        // AddSlackMessenger composes BOTH AddSlackInboundTransport
        // (Events API) AND AddSlackSocketModeTransport (Socket Mode)
        // internally, so every host that calls AddSlackMessenger
        // automatically wires ISlackInboundTransportFactory,
        // ISlackSocketModeConnectionFactory, SlackSocketModeOptions
        // (bound to Slack:SocketMode), and SlackInboundTransportHostedService
        // -- no per-host duplication.
        builder.Services.AddSlackMessenger(builder.Configuration);

        // Stage 3.2 / 4.1: mount the SlackAuthorizationFilter as a global
        // MVC service filter and opt the AgentSwarm.Messaging.Slack
        // assembly into MVC's application-part scanner so the three Slack
        // inbound controllers (Events / Commands / Interactions) surface
        // at /api/slack/{events,commands,interactions} regardless of
        // trimming / single-file / AOT publish semantics. The filter and
        // signature middleware share Slack:Signature:PathPrefix so a
        // single operator-tunable knob moves both the HMAC gate and the
        // ACL gate together (no configurable-prefix bypass footgun).
        builder.Services
            .AddControllers(options =>
            {
                options.Filters.AddService<SlackAuthorizationFilter>();
            })
            .AddSlackInboundControllers();

        // Stage 4.1: register the inbound HTTP transport services
        // (envelope factory, in-process ISlackInboundQueue, default
        // modal fast-path handler). The TryAdd-style bindings let a
        // future composition root swap in durable queue implementations
        // (Service Bus, SQL outbox/inbox) supplied by
        // AgentSwarm.Messaging.Core without changing this call site.
        builder.Services.AddSlackInboundTransport();

        // Stage 4.2: register the Socket Mode WebSocket transport
        // services (connection factory, transport-factory selector,
        // SlackSocketModeOptions) AND the
        // SlackInboundTransportHostedService that enumerates
        // ISlackWorkspaceConfigStore on host boot and starts the
        // appropriate transport per workspace. Binding builder.Configuration
        // exposes the Slack:SocketMode section so operators can override
        // reconnect bounds, ACK timeout, and receive-buffer size from
        // appsettings.json / environment variables without rebuilding.
        builder.Services.AddSlackSocketModeTransport(builder.Configuration);

        // Stage 4.1: swap the default in-process-only
        // ISlackFastPathIdempotencyStore for the durable two-level
        // composite (in-process L1 + EF L2 backed by the
        // slack_inbound_request_record table). Without this call the
        // modal fast-path falls back to in-memory dedup that does not
        // survive a process restart, allowing a Slack retry that
        // crosses a deployment to open a second modal for the same
        // trigger_id.
        builder.Services
            .AddSlackFastPathDurableIdempotency<SlackPersistenceDbContext>();

        // Stage 4.3: opt the Worker into the disk-backed dead-letter
        // queue BEFORE the ingestor wires its own
        // TryAdd<ISlackDeadLetterQueue, InMemorySlackDeadLetterQueue>
        // default. The in-memory default loses every exhausted-retry
        // envelope on a process restart, which contradicts the story's
        // FR-005 / FR-007 zero-loss requirement and the operator
        // attachment's "Reliability" cell. The directory is configurable
        // via Slack:Inbound:DeadLetterQueueDirectory and defaults to a
        // relative "data/slack-dead-letter" path so the wiring is never
        // accidentally skipped.
        string dlqDir = builder.Configuration["Slack:Inbound:DeadLetterQueueDirectory"]
            ?? "data/slack-dead-letter";
        if (!string.IsNullOrWhiteSpace(dlqDir))
        {
            builder.Services.AddFileSystemSlackDeadLetterQueue(dlqDir);
        }

        // Stage 4.3 (workstream:
        // ws-qq-slack-messenger-supp-phase-inbound-transport-stage-inbound-ingestor-and-deduplication):
        // register the SlackInboundIngestor BackgroundService and the
        // full processing pipeline (EF-backed idempotency guard,
        // envelope authorizer, retry policy, in-memory DLQ,
        // routing-by-source-type, audit recorder). Without this call
        // envelopes pushed onto ISlackInboundQueue by Stage 4.1 /
        // 4.2 transports would accumulate forever -- the BackgroundService
        // is the dedicated drainer required by Stage 4.3 of
        // implementation-plan.md. Must follow
        // AddSlackFastPathDurableIdempotency so the guard's EF
        // SlackInboundRequestRecord wiring is already in DI.
        builder.Services
            .AddSlackInboundIngestor<SlackPersistenceDbContext>();

        // Gate the no-op handler stand-ins on
        // Slack:Inbound:EnableDevelopmentHandlerStubs (defaults: true
        // in Development, false elsewhere). Production / Staging /
        // Testing hosts without real Stage 5 handlers therefore fail
        // loudly the first time the ingestor lazily resolves the
        // pipeline; the resolve-failure envelope is forwarded to the
        // last-resort ISlackInboundEnqueueDeadLetterSink so nothing
        // is lost. The host itself still starts cleanly so health
        // probes, signature middleware, and the audit schema
        // bootstrap remain available. Operators override the gate
        // explicitly via configuration.
        //
        // TODO(qq:SLACK-MESSENGER-SUPP Stage 5.x): replace this gate
        // with real handler registrations (5.1 command dispatcher,
        // 5.2 @mention dispatcher, 5.3 interaction -> HumanDecisionEvent
        // dispatcher) and delete both the constant and the opt-in
        // call. The production Worker MUST NOT ship the no-op stubs
        // once real handlers exist.
        if (ShouldEnableDevelopmentHandlerStubs(builder))
        {
            builder.Services.AddSlackInboundDevelopmentHandlerStubs();
        }

        // Stage 4.1: opt the Worker into the durable file-system
        // dead-letter sink for post-ACK enqueue failures. The default
        // registration inside AddSlackInboundTransport is
        // InMemorySlackInboundEnqueueDeadLetterSink, which loses
        // captured envelopes on process restart -- FR-005 / FR-007
        // "no message loss" requires durable persistence so a worker
        // restart cannot erase the recovery log. Hosts configure the
        // destination via Slack:Inbound:DeadLetterDirectory; the
        // default value points at a relative "data/slack-inbound-dead-letter"
        // path so a missing config does NOT silently fall back to
        // in-memory.
        string deadLetterDir = builder.Configuration["Slack:Inbound:DeadLetterDirectory"]
            ?? "data/slack-inbound-dead-letter";
        if (!string.IsNullOrWhiteSpace(deadLetterDir))
        {
            builder.Services.AddFileSystemSlackInboundEnqueueDeadLetterSink(deadLetterDir);
        }

        WebApplication app = builder.Build();

        // Stage 4.1: fail-fast at host startup when the resolved
        // ISlackInboundQueue is the in-process
        // ChannelBasedSlackInboundQueue and the deployment claims to
        // be Production. Operators who have validated that an
        // in-memory queue is acceptable for their deployment can
        // bypass the guard by setting
        // 'Slack:Inbound:Queue:AllowInMemoryInProduction=true'.
        app.Services.EnsureDurableInboundQueueForProduction(
            app.Environment,
            app.Configuration);

        // Stage 3.1: ensure the durable Slack audit schema is provisioned
        // BEFORE the first inbound request. SQLite is the default backing
        // store and the file may not exist on a fresh deployment;
        // EnsureCreated is the idempotent bootstrap that gives the EF
        // audit writer a table to insert into.
        using (IServiceScope scope = app.Services.CreateScope())
        {
            SlackPersistenceDbContext ctx =
                scope.ServiceProvider.GetRequiredService<SlackPersistenceDbContext>();
            ctx.Database.EnsureCreated();
        }

        // Stage 3.1: eagerly resolve the composite ISecretProvider so a
        // misconfigured SecretProvider:ProviderType (e.g. KeyVault
        // without a registered backend) fails at host start, not at
        // the first inbound Slack request.
        _ = app.Services.GetRequiredService<ISecretProvider>();

        // Stage 3.1: signature verification middleware. Placed before
        // endpoint mapping so the inbound Slack routes inherit the HMAC
        // gate; the middleware short-circuits any path outside
        // SlackSignatureOptions.PathPrefix (default /api/slack), so the
        // health probes below are not affected.
        app.UseSlackSignatureValidation();

        // Stage 7.3: mount the real Slack health-check endpoints
        // (/health/ready, /health/live) with operator-configurable
        // paths via Slack:Health:ReadyEndpointPath /
        // Slack:Health:LiveEndpointPath.
        app.MapSlackHealthEndpoints();

        // Map controllers so the SlackAuthorizationFilter applied
        // globally above is invoked for every controller endpoint.
        app.MapControllers();

        return app;
    }

    private static void AddSlackAuditPersistence(WebApplicationBuilder builder)
    {
        // Resolve the connection string LAZILY via the runtime
        // IServiceProvider so any IConfiguration overrides applied by a
        // WebApplicationFactory<Program> hook (e.g., the Stage 3.1
        // integration tests' per-test isolated SQLite path) are honoured
        // -- a builder-time read of builder.Configuration would lock in
        // the appsettings.json default before the hook runs.
        builder.Services.AddDbContext<SlackPersistenceDbContext>((sp, opts) =>
        {
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            string? connectionString = cfg.GetConnectionString(SlackAuditConnectionStringKey);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = "Data Source=slack-audit.db";
            }

            opts.UseSqlite(connectionString);
        });

        builder.Services.AddSlackEntityFrameworkAuditWriter<SlackPersistenceDbContext>();
    }

    /// <summary>
    /// Resolves the opt-in gate for the no-op Slack handler stand-ins.
    /// Reads the <see cref="EnableDevelopmentHandlerStubsKey"/> value
    /// as a boolean; if absent or unparseable, defaults to
    /// <see cref="HostEnvironmentEnvExtensions.IsDevelopment(Microsoft.Extensions.Hosting.IHostEnvironment)"/>.
    /// The gate is environment-defaulted, not environment-locked, so
    /// an operator can force the stubs on in Production for a smoke
    /// test or force them off on a dev laptop for a fail-fast check.
    /// </summary>
    internal static bool ShouldEnableDevelopmentHandlerStubs(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string? raw = builder.Configuration[EnableDevelopmentHandlerStubsKey];
        if (!string.IsNullOrWhiteSpace(raw) && bool.TryParse(raw, out bool explicitValue))
        {
            return explicitValue;
        }

        return builder.Environment.IsDevelopment();
    }
}
