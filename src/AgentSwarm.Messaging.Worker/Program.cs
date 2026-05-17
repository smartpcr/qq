using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Observability;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AgentSwarm.Messaging.Worker;

/// <summary>
/// Entry point for the AgentSwarm messaging gateway host. The host owns the
/// HTTP surface used by Slack (and other connector) inbound endpoints and
/// runs the background message-processing pipeline. Stage 3.1 wires in the
/// <see cref="SlackSignatureValidator"/> middleware so every request to
/// <c>/api/slack</c> is HMAC-verified before later stages add the
/// authorization filter, idempotency guard, and command handlers.
/// </summary>
public class Program
{
    /// <summary>
    /// Configuration key for the SQLite (or other relational) connection
    /// string backing <see cref="SlackPersistenceDbContext"/>. Exposed as
    /// a constant so the integration tests assert against the same key
    /// the host actually reads.
    /// </summary>
    public const string SlackAuditConnectionStringKey = "SlackAudit";

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
        builder.Services.AddRouting();
        builder.Services.AddSlackConnectorOptions(builder.Configuration);

        // Stage 7.2 (workstream:
        // ws-qq-slack-messenger-supp-phase-observability-and-operations-stage-opentelemetry-traces-and-metrics):
        // surface the Slack connector's `ActivitySource` / `Meter`
        // primitives in DI BEFORE any downstream registration so the
        // OpenTelemetry .NET SDK (when the host opts in) can resolve
        // them by injected instance and so any test composition root
        // sees the same singletons production code emits to. The
        // call is idempotent -- TryAddSingleton means re-registration
        // by a sibling composition root never produces a second
        // instance. The ActivitySource is named
        // "AgentSwarm.Messaging.Slack" and the Meter shares the same
        // name (per architecture.md §6.3 / tech-spec.md §2.6).
        builder.Services.AddSlackTelemetry();

        // Stage 3.1: register the secret-provider chain BEFORE the
        // signature middleware so the appsettings
        // SecretProvider.ProviderType selector is honoured. The Slack
        // validator uses TryAddSingleton<ISecretProvider, ...> as a
        // fallback only, so this AddSecretProvider call wins.
        builder.Services.AddSecretProvider(builder.Configuration);

        // Stage 3.1: register the durable Slack audit DbContext and EF
        // writer BEFORE AddSlackSignatureValidation so the EF writer
        // wins over the in-memory writer that the validator's
        // TryAddSingleton fallback would otherwise register. Without
        // this call signature-rejection audit rows are lost on restart.
        AddSlackAuditPersistence(builder);

        // Stage 7.1 (workstream:
        // ws-qq-slack-messenger-supp-phase-observability-and-operations-stage-audit-logging-and-retention):
        // replace the bare EF audit writer with the broader
        // SlackAuditLogger<TContext>. The logger implements BOTH
        // ISlackAuditLogger (LogAsync + QueryAsync per architecture
        // §4.6) and ISlackAuditEntryWriter (the Stage 3.1 append seam)
        // so every existing pipeline call site -- signature validation,
        // authorization, idempotency, command dispatch, interaction
        // handling, modal open, outbound dispatch, thread manager,
        // and the DirectApiClient -- automatically routes through
        // LogAsync via the explicit AppendAsync forwarder on the
        // logger. The same extension also registers the
        // SlackRetentionCleanupService BackgroundService that purges
        // slack_audit_entry and slack_inbound_request_record rows
        // older than 30 days per tech-spec §2.7 / resolved OQ-2.
        // Bind Slack:Retention from appsettings so operators can tune
        // the cadence and retention window without rebuilding.
        builder.Services.AddSlackAuditLogger<SlackPersistenceDbContext>(builder.Configuration);

        // Stage 3.1 (evaluator iter-3 item 1): register the EF-backed
        // ISlackWorkspaceConfigStore so a restarted Worker can resolve
        // SlackWorkspaceConfig.SigningSecretRef directly from the
        // durable slack_workspace_config table -- not from a transient
        // in-memory dictionary that only survives until the next
        // restart. RemoveAll + AddSingleton ensures the EF store wins
        // regardless of the order other extensions are called in.
        builder.Services.AddSlackEntityFrameworkWorkspaceConfigStore<SlackPersistenceDbContext>();

        // Stage 3.1 (evaluator iter-3 item 1): register the startup
        // seeder that upserts every Slack:Workspaces entry from
        // appsettings into the EF-backed table. The seeder is
        // idempotent: existing rows are updated in-place (preserving
        // created_at), missing rows are inserted, and rows that exist
        // only in the database are left untouched so operators can
        // manage workspaces outside of appsettings without the seeder
        // stomping them. This keeps appsettings a convenient bootstrap
        // source while making the database the source of truth.
        builder.Services.AddSlackWorkspaceConfigSeeder<SlackPersistenceDbContext>(builder.Configuration);

        // Stage 3.1 (evaluator iter-1 item 4): also bind the seed
        // options so the iter-1 in-memory store remains usable when a
        // composition root opts out of the EF wiring above. With the
        // EF store registered first (RemoveAll wins), this call is
        // effectively a no-op for the canonical Worker host -- but it
        // keeps the API surface stable for test hosts and downstream
        // composition roots that prefer pure in-memory wiring.
        builder.Services.AddSlackWorkspaceConfigStoreFromConfiguration(builder.Configuration);

        // Stage 3.1: register the signature validator together with its
        // workspace-config store and audit-entry-backed sink. The
        // extension uses TryAdd*, so any production overrides (the
        // database-backed workspace store added by Stage 2.3 or the
        // EntityFrameworkSlackAuditEntryWriter added by the persistence
        // composition root) registered before this call win.
        builder.Services.AddSlackSignatureValidation(builder.Configuration);

        // Stage 3.2: register the authorization filter (workspace ->
        // channel -> user-group ACL) and its supporting services
        // (ISlackMembershipResolver, ISlackUserGroupClient,
        // ISlackAuthorizationAuditSink). All registrations use
        // TryAdd*, so the in-memory fall-backs only apply when no
        // production override is already present (the EF
        // ISlackWorkspaceConfigStore and EF ISlackAuditEntryWriter
        // registered above win for the Worker host).
        builder.Services.AddSlackAuthorization(builder.Configuration);

        // Stage 3.2: mount the SlackAuthorizationFilter on the MVC
        // pipeline as a global service filter. Controllers land in
        // Stage 4.1, but registering the filter at the
        // composition-root level now (rather than waiting for Stage
        // 4.1) means every controller added later automatically
        // inherits the ACL gate without needing to decorate each
        // controller with [ServiceFilter(typeof(SlackAuthorizationFilter))].
        // The filter is path-scoped via SlackSignatureOptions.PathPrefix
        // (default '/api/slack'): SlackAuthorizationFilter consumes
        // the SAME option the upstream HMAC middleware does, so the
        // signature gate and the ACL gate are mathematically guaranteed
        // to enforce on the same URL surface. An operator who moves
        // Slack:Signature:PathPrefix to /slack-gateway moves the
        // authorization filter along with it -- there is no separate
        // Slack:Authorization:PathPrefix to forget, which was the
        // configurable-prefix authorization-bypass footgun called out
        // by the iter-2 evaluator. Non-Slack MVC endpoints (admin
        // APIs, cache invalidation, controller-mounted health probes)
        // short-circuit out of the ACL without parsing the body or
        // looking up the workspace. The signature middleware
        // (UseSlackSignatureValidation below) runs first and stamps
        // HttpContext.Items with the resolved SlackWorkspaceConfig,
        // which the filter re-uses to avoid a second workspace
        // lookup.
        builder.Services
            .AddControllers(options =>
            {
                options.Filters.AddService<SlackAuthorizationFilter>();
            })

            // Stage 4.1 iter-2 evaluator item 2: explicitly opt the
            // Slack controllers' assembly into MVC's application-part
            // scanner. The three controllers
            // (SlackEventsController, SlackCommandsController,
            // SlackInteractionsController) live in the
            // AgentSwarm.Messaging.Slack assembly, not the Worker's
            // entry assembly; AddSlackInboundControllers wires the
            // ApplicationPart so MVC's controller-feature scanner
            // discovers them deterministically across every host
            // (Production Worker, WebApplicationFactory<Program>
            // integration tests, future hosts that reuse Program).
            // Without this call discovery depends on transitive
            // assembly-load semantics, which is fragile under
            // trimming, single-file publish, and AOT.
            .AddSlackInboundControllers();

        // Stage 4.1: register the three Slack inbound HTTP controllers
        // (SlackEventsController, SlackCommandsController,
        // SlackInteractionsController), the SlackInboundEnvelopeFactory
        // they share, the in-process ChannelBasedSlackInboundQueue
        // implementation of ISlackInboundQueue, the post-ACK
        // dead-letter sink, the default modal fast-path handler, and
        // the no-op interaction fast-path handler. AddSlackInboundTransport
        // uses TryAdd* throughout so any production override
        // registered above (e.g. a durable Service Bus
        // ISlackInboundQueue, a dedicated dead-letter sink) wins.
        builder.Services.AddSlackInboundTransport();

        // Stage 4.1 (iter-4 evaluator item 1): swap the in-memory
        // dead-letter sink registered by AddSlackInboundTransport for
        // the durable file-system sink. The directory is read from
        // Slack:Inbound:DeadLetterDirectory (defaults to
        // data/slack-inbound-dead-letter, see appsettings.json) so an
        // operator can point the JSONL spill at an attached volume or
        // shared file system without recompiling. Without this call
        // post-ACK enqueue failures captured before a Worker restart
        // would vanish along with the in-memory ring buffer, violating
        // FR-005 / FR-007 "no message loss" from
        // agent_swarm_messenger_user_stories.md.
        string inboundDeadLetterDirectory = builder.Configuration
            .GetValue<string?>("Slack:Inbound:DeadLetterDirectory")
            ?? "data/slack-inbound-dead-letter";
        builder.Services.AddFileSystemSlackInboundEnqueueDeadLetterSink(inboundDeadLetterDirectory);

        // Stage 4.3: wire the SlackInboundIngestor BackgroundService
        // that drains ISlackInboundQueue and runs the idempotency +
        // authorization + dispatch pipeline asynchronously. The Stage
        // 4.1 controllers ACK Slack within the 3-second budget and
        // hand the envelope to the ingestor via the queue; the
        // ingestor is registered here so the Worker host's composition
        // root owns the full inbound pipeline end-to-end. The Stage
        // 5.x command / app-mention / interaction handlers are not
        // yet implemented in this host, so we explicitly opt into the
        // development handler stubs to keep the ingestor resolvable;
        // production deployments replace AddSlackInboundDevelopmentHandlerStubs
        // with the real handler registrations once Stage 5 ships.
        builder.Services.AddSlackInboundIngestor<SlackPersistenceDbContext>();
        builder.Services.AddSlackInboundDevelopmentHandlerStubs();

        WebApplication app = builder.Build();

        // Stage 4.1 iter-2 evaluator item 3: fail-fast at host startup
        // when the resolved ISlackInboundQueue is the in-process
        // ChannelBasedSlackInboundQueue and the deployment claims to
        // be Production. The in-process channel does not survive a
        // pod restart, so allowing Production to boot with it would
        // silently break FR-005 / FR-007 "no message loss" from
        // agent_swarm_messenger_user_stories.md. Operators who have
        // validated that an in-memory queue is acceptable for their
        // deployment (e.g., a single-instance staging clone) can
        // bypass the guard by setting
        // 'Slack:Inbound:Queue:AllowInMemoryInProduction=true'.
        // Development, Staging, and Testing environments are
        // unaffected -- the guard is a no-op outside Production so
        // local dev and CI keep working.
        app.Services.EnsureDurableInboundQueueForProduction(
            app.Environment,
            app.Configuration);

        // Stage 3.1: ensure the durable Slack audit schema is provisioned
        // BEFORE the first inbound request. SQLite is the default backing
        // store (see SlackAuditConnectionStringKey) and the file may not
        // exist on a fresh deployment; EnsureCreated is the idempotent
        // bootstrap that gives the EF audit writer a table to insert into.
        // Without this call the first signature-rejection write would
        // throw "no such table: slack_audit_entry" and the rejection
        // would never reach durable storage.
        using (IServiceScope scope = app.Services.CreateScope())
        {
            SlackPersistenceDbContext ctx =
                scope.ServiceProvider.GetRequiredService<SlackPersistenceDbContext>();
            ctx.Database.EnsureCreated();
        }

        // Stage 3.1 (evaluator iter-3 item 3): eagerly resolve the
        // composite ISecretProvider so a misconfigured
        // SecretProvider:ProviderType (e.g. KeyVault without a
        // registered backend) fails at host start, not at the first
        // inbound Slack request. CompositeSecretProvider throws
        // InvalidOperationException for unsupported provider types --
        // resolving it here surfaces the error in the host start log
        // where an operator will see it.
        _ = app.Services.GetRequiredService<ISecretProvider>();

        // Stage 3.1: signature verification middleware. Placed before
        // endpoint mapping so the inbound Slack routes added by later
        // stages inherit the HMAC gate; the middleware short-circuits
        // any path outside SlackSignatureOptions.PathPrefix (default
        // /api/slack), so the health probes below are not affected.
        app.UseSlackSignatureValidation();

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

        // Stage 3.2: map controller endpoints so the
        // SlackAuthorizationFilter registered as a global MVC filter
        // above is invoked when Stage 4.1 lands the inbound Slack
        // controllers. Stage 4.1 only needs to add controller types
        // -- no further composition-root edits are required.
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
}
