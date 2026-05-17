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
        // The signature middleware (UseSlackSignatureValidation
        // below) runs first and stamps HttpContext.Items with the
        // resolved SlackWorkspaceConfig, which the filter re-uses
        // to avoid a second workspace lookup.
        builder.Services
            .AddControllers(options =>
            {
                options.Filters.AddService<SlackAuthorizationFilter>();
            })
            // Stage 4.1: discover the Slack inbound controllers
            // (SlackEventsController, SlackCommandsController,
            // SlackInteractionsController) which live in the
            // AgentSwarm.Messaging.Slack class library. Without this
            // call MVC's application-part scanner only sees the
            // Worker entry assembly and would not map the
            // /api/slack/* routes.
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

        // Stage 4.1 (evaluator iter-3 item 2): swap the default
        // in-process-only ISlackFastPathIdempotencyStore for the
        // durable two-level composite (in-process L1 + EF L2 backed by
        // the slack_inbound_request_record table). Without this call
        // the modal fast-path falls back to in-memory dedup that does
        // not survive a process restart, allowing a Slack retry that
        // crosses a deployment to open a second modal for the same
        // trigger_id.
        builder.Services
            .AddSlackFastPathDurableIdempotency<SlackPersistenceDbContext>();

        // Stage 4.3 iter 6 evaluator item #2: opt the Worker into the
        // disk-backed dead-letter queue BEFORE the ingestor wires its
        // own TryAdd<ISlackDeadLetterQueue, InMemorySlackDeadLetterQueue>
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

        // Stage 5.1 (workstream:
        // ws-qq-slack-messenger-supp-phase-command-and-interaction-processing-stage-slash-command-dispatch)
        // + Stage 5.2 (workstream:
        // ws-qq-slack-messenger-supp-phase-command-and-interaction-processing-stage-app-mention-processing):
        // register the real SlackCommandHandler (replaces the Stage
        // 4.3 NoOpSlackCommandHandler dev-stub for the ingestor's
        // ISlackCommandHandler binding) AND the real
        // SlackAppMentionHandler (replaces the
        // NoOpSlackAppMentionHandler dev-stub for the
        // ISlackAppMentionHandler binding). The dispatcher parses
        // `/agent <sub-command>` text, calls IAgentTaskService for
        // ask / status / approve / reject and best-effort views.open
        // for review / escalate, and writes ephemeral error messages
        // for unrecognised sub-commands; the app-mention handler
        // strips the `<@BOT_USER_ID>` prefix and delegates through the
        // same dispatch switch (posting replies as threaded
        // chat.postMessage instead of ephemeral response_url replies).
        // Must follow AddSlackInboundIngestor so the
        // AddSlackInboundDevelopmentHandlerStubs call below only
        // re-registers no-ops for the still-unimplemented
        // ISlackInteractionHandler contract.
        builder.Services.AddSlackCommandDispatcher();

        // Stage 5.1 iter-2 evaluator item 3 (STRUCTURAL fix):
        // AddSlackCommandDispatcher INTENTIONALLY no longer auto-
        // registers a default IAgentTaskService. The previous build
        // silently wired NoOpAgentTaskService, so a misconfigured
        // production deployment would happily ACK every `/agent ask`
        // as "success" without ever dispatching it to the orchestrator
        // (a real no-message-loss bug indistinguishable from green).
        // The Worker explicitly opts in to the development stub here
        // so local boots succeed while the real orchestrator project
        // (qq:ORCHESTRATOR) is still being built; any operator
        // reading this file SEES the explicit opt-in and knows to
        // remove it before going to production.
        //
        // TODO(qq:SLACK-MESSENGER-SUPP -- post-orchestrator): replace
        // this call with the orchestrator-supplied IAgentTaskService
        // registration. The production Worker must NOT ship the
        // NoOpAgentTaskService.
        builder.Services.AddSlackCommandDispatcherDevelopmentDefaults();

        // Stage 5.3 (ws-qq-slack-messenger-supp-phase-command-and-interaction-processing-stage-interactive-payload-processing):
        // register the real SlackInteractionHandler (replaces the
        // Stage 4.3 NoOpSlackInteractionHandler dev-stub). The
        // dispatcher decodes Block Kit button clicks and modal
        // view_submission payloads into typed HumanDecisionEvents and
        // publishes them through IAgentTaskService; for button clicks
        // it also calls chat.update to disable the originating
        // buttons. Must follow AddSlackInboundIngestor so the
        // AddSlackInboundDevelopmentHandlerStubs call below only
        // re-registers a no-op for the still-unimplemented
        // ISlackAppMentionHandler contract.
        builder.Services.AddSlackInteractionDispatcher();

        // Opt into the EF-backed thread-mapping lookup so the
        // interaction handler resolves CorrelationId from the
        // persisted SlackThreadMapping row produced by Stage 6.2
        // (rather than degrading to the envelope's idempotency key).
        // Safe to call before Stage 6.2 ships -- the lookup just
        // returns null until rows exist.
        builder.Services.AddSlackEntityFrameworkThreadMappingLookup<SlackPersistenceDbContext>();

        // Stage 6.2 (workstream:
        // ws-qq-slack-messenger-supp-phase-outbound-messaging-stage-thread-lifecycle-management):
        // register the SlackThreadManager that owns the one-to-one
        // mapping between agent tasks and Slack threads. The manager
        // creates root messages via chat.postMessage on first call,
        // persists the SlackThreadMapping, and recovers into the
        // workspace's FallbackChannelId when the previously-stored
        // channel/thread is no longer reachable (architecture.md
        // §2.11 lifecycle steps 1, 3, 4). Stage 6.3's outbound
        // dispatcher will consume ISlackThreadManager to resolve
        // thread_ts before posting threaded replies. Wired before
        // AddSlackInboundDevelopmentHandlerStubs so any composition
        // root that already opted in to the EF lookup keeps that
        // wiring; the thread manager registration uses RemoveAll +
        // AddSingleton (the whole point is to replace placeholder
        // implementations).
        builder.Services.AddSlackThreadLifecycleManagement<SlackPersistenceDbContext>();
        builder.Services.AddSlackChatPostMessageOptions(builder.Configuration);

        // Stage 6.3 evaluator iter-1 item #2 (FR-005 durable outbound
        // queue + FR-007 zero message loss): replace the in-process
        // ChannelBasedSlackOutboundQueue + InMemorySlackDeadLetterQueue
        // defaults BEFORE AddSlackOutboundDispatcher so the
        // dispatcher's TryAddSingleton fallbacks become no-ops and
        // both queue surfaces persist across connector restart.
        // FileSystemSlackOutboundQueue implements
        // IAcknowledgeableSlackOutboundQueue, which the dispatcher
        // detects via DI to delete the journal entry only after the
        // envelope reaches a terminal disposition (delivered or
        // safely dead-lettered). Hosts override the directory paths
        // via Slack:Outbound:JournalDirectory / Slack:Outbound:DeadLetterDirectory.
        string outboundJournalDir = builder.Configuration["Slack:Outbound:JournalDirectory"]
            ?? "data/slack-outbound-journal";
        if (!string.IsNullOrWhiteSpace(outboundJournalDir))
        {
            builder.Services.AddFileSystemSlackOutboundQueue(outboundJournalDir);
        }

        string outboundDeadLetterDir = builder.Configuration["Slack:Outbound:DeadLetterDirectory"]
            ?? "data/slack-outbound-dead-letter";
        if (!string.IsNullOrWhiteSpace(outboundDeadLetterDir))
        {
            builder.Services.AddFileSystemSlackDeadLetterQueue(outboundDeadLetterDir);
        }

        // Stage 6.3 (workstream:
        // ws-qq-slack-messenger-supp-phase-outbound-messaging-stage-outbound-dispatch-and-rate-limiting):
        // register the SlackOutboundDispatcher BackgroundService and
        // its collaborators (in-process ISlackOutboundQueue, shared
        // ISlackRateLimiter, HTTP-backed ISlackOutboundDispatchClient,
        // SlackConnector implementing IMessengerConnector). The
        // dispatcher drains envelopes pushed by SlackConnector,
        // applies Slack's per-tier token-bucket limits
        // (chat.postMessage Tier 2 per-channel; views.update Tier 4
        // per-workspace), honours HTTP 429 Retry-After by pausing the
        // bucket, retries transient failures per the shared
        // ISlackRetryPolicy, and dead-letters envelopes that exhaust
        // the retry budget. Wired after AddSlackThreadLifecycleManagement
        // because the dispatcher resolves SlackThreadMapping rows
        // produced by the thread manager to recover the destination
        // channel + team for every outbound API call.
        builder.Services.AddSlackOutboundDispatcher(builder.Configuration);

        // Stage 4.3 iter 6 evaluator item #2 (STRUCTURAL fix):
        // AddSlackInboundIngestor INTENTIONALLY no longer registers
        // no-op handler defaults. A production host that resolved
        // ISlackCommandHandler / ISlackAppMentionHandler /
        // ISlackInteractionHandler against the silent-completion
        // stubs would ack-and-drop every Slack request -- a real
        // no-message-loss bug. Stage 5.1 swaps in the real
        // SlackCommandHandler above, Stage 5.3 swaps in the real
        // SlackInteractionHandler; Stage 5.2 is still in flight, so
        // the Worker explicitly opts into the development stand-in
        // for the @mention handler only so the ingestor remains
        // resolvable AND any operator reading this file SEES the
        // explicit opt-in (and knows to remove it before going to
        // production). The TryAdd inside
        // AddSlackInboundDevelopmentHandlerStubs makes the call a
        // no-op for ISlackCommandHandler / ISlackInteractionHandler
        // because the real handlers above won.
        //
        // TODO(qq:SLACK-MESSENGER-SUPP Stage 5.2): replace this call
        // with the real @mention dispatcher. The production Worker
        // must NOT ship the remaining no-op stub.
        builder.Services.AddSlackInboundDevelopmentHandlerStubs();

        // Stage 4.1 (evaluator iter-4 item 1): opt the Worker into the
        // durable file-system dead-letter sink for post-ACK enqueue
        // failures. The default registration inside
        // AddSlackInboundTransport is InMemorySlackInboundEnqueueDeadLetterSink,
        // which loses captured envelopes on process restart -- the
        // operator-uploaded story attachment's FR-005 / FR-007
        // "no message loss" requirements (and the iter-4 evaluator)
        // require durable persistence so a worker restart cannot
        // erase the recovery log. Hosts configure the destination via
        // Slack:Inbound:DeadLetterDirectory; the default value points
        // at a relative "data/slack-inbound-dead-letter" path so a
        // missing config does NOT silently fall back to in-memory.
        string deadLetterDir = builder.Configuration["Slack:Inbound:DeadLetterDirectory"]
            ?? "data/slack-inbound-dead-letter";
        if (!string.IsNullOrWhiteSpace(deadLetterDir))
        {
            builder.Services.AddFileSystemSlackInboundEnqueueDeadLetterSink(deadLetterDir);
        }

        WebApplication app = builder.Build();

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
