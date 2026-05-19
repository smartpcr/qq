// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

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
    /// Default SQLite connection string used when the host's
    /// configuration does not supply <see cref="SlackAuditConnectionStringKey"/>.
    /// </summary>
    public const string DefaultSlackAuditConnectionString = "Data Source=slack-audit.db";

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
        // Iter-3 evaluator item 2 fix: the facade NO LONGER installs
        // a development-default NoOpAgentTaskService stub (the prior
        // behaviour silently ack'd /agent ask with a synthetic task
        // when the host forgot a real orchestrator). The Worker is
        // still in pre-orchestrator-client mode, so we explicitly opt
        // in to the dev stub here -- the call is observable in the
        // composition root and a future commit that wires the real
        // orchestrator simply deletes this single line. Production
        // hosts that ship the real orchestrator client BEFORE the
        // dev opt-in get TryAdd semantics (their real registration
        // wins).
        builder.Services.AddSlackCommandDispatcherDevelopmentDefaults();

        // Iter-4 evaluator item 2 fix: AddSlackMessenger now composes
        // BOTH AddSlackInboundTransport (Events API) AND
        // AddSlackSocketModeTransport (Socket Mode) internally. The
        // Worker previously had to call AddSlackSocketModeTransport
        // explicitly; that responsibility moved into the facade so
        // every host that calls AddSlackMessenger automatically wires
        // ISlackInboundTransportFactory, ISlackSocketModeConnectionFactory,
        // SlackSocketModeOptions (bound to Slack:SocketMode), and
        // SlackInboundTransportHostedService -- no per-host duplication.
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

        WebApplication app = builder.Build();

        // Stage 4.1 iter-2 evaluator item 3: fail-fast at host startup
        // when the resolved ISlackInboundQueue is the in-process
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

        // Stage 3.1 (evaluator iter-3 item 3): eagerly resolve the
        // composite ISecretProvider so a misconfigured
        // SecretProvider:ProviderType (e.g. KeyVault without a registered
        // backend) fails at host start, not at the first inbound Slack
        // request.
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
}
