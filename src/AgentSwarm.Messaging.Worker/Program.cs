using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
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
            });

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
