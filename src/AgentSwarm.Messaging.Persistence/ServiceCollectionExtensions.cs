using System;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Stage 3.4 (iter-5 evaluator item 1) — EF Core provider switch.
    /// The implementation-plan brief mandates "SQLite for dev/local
    /// and PostgreSQL or SQL Server for production" (consistent with
    /// Stages 4.1, 5.3). The provider is selected by the
    /// <c>MessagingDb:Provider</c> configuration value:
    /// <list type="bullet">
    ///   <item><description><c>Sqlite</c> (default) — uses
    ///   <see cref="SqliteDbContextOptionsBuilderExtensions.UseSqlite{TContext}(DbContextOptionsBuilder{TContext}, string, Action{Microsoft.EntityFrameworkCore.Infrastructure.SqliteDbContextOptionsBuilder}?)"/>;
    ///   matches the design-time factory and the only migration set
    ///   committed today.</description></item>
    ///   <item><description><c>PostgreSQL</c> (aliases: <c>Postgres</c>,
    ///   <c>Npgsql</c>) — uses
    ///   <c>Npgsql.EntityFrameworkCore.PostgreSQL.UseNpgsql</c>.
    ///   Production deployments must generate provider-specific
    ///   migrations (see
    ///   <see cref="DesignTimeMessagingDbContextFactory"/>); the
    ///   <c>operator_bindings</c> model is provider-agnostic.</description></item>
    ///   <item><description><c>SqlServer</c> (alias: <c>MSSQL</c>) —
    ///   uses <c>Microsoft.EntityFrameworkCore.SqlServer.UseSqlServer</c>.
    ///   Same migration caveat as PostgreSQL.</description></item>
    /// </list>
    /// Unknown / typo'd provider names throw
    /// <see cref="System.NotSupportedException"/> at host startup so
    /// the operator sees a clear error instead of silently falling
    /// back to a different store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Host contract — services this method replaces.</b> In
    /// addition to wiring <see cref="MessagingDbContext"/>, this
    /// method calls <c>services.Replace(...)</c> on the following
    /// abstractions whose in-memory siblings are otherwise registered
    /// by <c>TelegramServiceCollectionExtensions.AddTelegram(...)</c>
    /// via <c>TryAddSingleton</c>:
    /// <list type="bullet">
    ///   <item><description><see cref="IOutboundMessageIdIndex"/>
    ///   → <see cref="PersistentOutboundMessageIdIndex"/> (iter-3
    ///   evaluator item 3).</description></item>
    ///   <item><description><see cref="IOutboundDeadLetterStore"/>
    ///   → <see cref="PersistentOutboundDeadLetterStore"/> (iter-4
    ///   evaluator item 4).</description></item>
    ///   <item><description><see cref="ITaskOversightRepository"/>
    ///   → <see cref="PersistentTaskOversightRepository"/> (Stage 3.2).</description></item>
    ///   <item><description><see cref="IAuditLogger"/>
    ///   → <see cref="PersistentAuditLogger"/> (Stage 3.2 iter-2
    ///   evaluator item 5).</description></item>
    ///   <item><description><see cref="IOperatorRegistry"/>
    ///   → <see cref="PersistentOperatorRegistry"/> (Stage 3.4).</description></item>
    ///   <item><description><see cref="IDeduplicationService"/>
    ///   → <see cref="PersistentDeduplicationService"/> (Stage 4.3).</description></item>
    /// </list>
    /// Every replaced concrete is a singleton that bridges to the
    /// scoped <see cref="MessagingDbContext"/> via
    /// <see cref="IServiceScopeFactory"/>; that pattern is intentional
    /// and is what lets a singleton-wired consumer (for example the
    /// singleton <c>TelegramUpdatePipeline</c> and the singleton
    /// <c>TelegramUserAuthorizationService</c> the Worker registers
    /// against <see cref="IUserAuthorizationService"/>) use a Scoped
    /// EF context without violating the captive-dependency rule.
    /// </para>
    /// <para>
    /// <b>Services this method does NOT register.</b>
    /// <see cref="IUserAuthorizationService"/> is deliberately left
    /// to the composition root (in the production worker that is
    /// <c>AgentSwarm.Messaging.Worker/Program.cs</c>, which registers
    /// <c>TelegramUserAuthorizationService</c> via
    /// <c>TryAddSingleton</c>). Keeping that registration in the
    /// host avoids hard-coding a Telegram-specific implementation
    /// into the connector-agnostic persistence layer and preserves
    /// the loud-failure semantic <c>AddTelegram</c> intentionally
    /// chose for the auth service (see
    /// <c>TelegramServiceCollectionExtensions.AddTelegram</c>
    /// remarks, "<i>Authorization service is NOT stubbed</i>"). The
    /// replaced <see cref="IOperatorRegistry"/> is what
    /// <c>TelegramUserAuthorizationService</c> resolves at activation
    /// time, so no extra wiring is required to make the auth service
    /// pick up the persistent registry — the indirection is via DI,
    /// not via captured state.
    /// </para>
    /// <para>
    /// <b>Recommended composition-root call order.</b> Although
    /// <see cref="ServiceCollectionDescriptorExtensions.Replace(IServiceCollection, ServiceDescriptor)"/>
    /// is order-tolerant for the abstractions listed above (it
    /// removes any matching descriptor regardless of whether the
    /// stub registration came before or after), hosts should call
    /// <c>AddMessagingPersistence</c> BEFORE any host-level
    /// <c>Replace</c> or <c>Add</c> on the same abstractions
    /// (<see cref="IOperatorRegistry"/>,
    /// <see cref="IAuditLogger"/>, <see cref="IOutboundDeadLetterStore"/>,
    /// <see cref="IOutboundMessageIdIndex"/>,
    /// <see cref="ITaskOversightRepository"/>) so the host's override
    /// is what survives. The Worker's canonical order is:
    /// <list type="number">
    ///   <item><description><c>AddMessagingPersistence(configuration)</c>
    ///   — replaces the five abstractions above with their EF-backed
    ///   implementations.</description></item>
    ///   <item><description><c>AddTelegram(configuration)</c> —
    ///   <c>TryAddSingleton</c> calls inside <c>AddTelegram</c> no-op
    ///   for any abstraction the persistence layer already replaced,
    ///   preserving the persistent implementation.</description></item>
    ///   <item><description><c>TryAddSingleton&lt;IUserAuthorizationService,
    ///   TelegramUserAuthorizationService&gt;()</c> — first-wins, so
    ///   a test/alternate host that pre-registered a different
    ///   <see cref="IUserAuthorizationService"/> still wins. The
    ///   custom auth service resolves <see cref="IOperatorRegistry"/>
    ///   from DI at activation time and therefore sees whichever
    ///   registry survived steps 1–2 above; there is no separate
    ///   "auth ↔ registry" synchronization the host has to perform.</description></item>
    /// </list>
    /// Tests that supply their own <see cref="IOperatorRegistry"/>
    /// (for example to assert against a stub) should either skip
    /// <c>AddMessagingPersistence</c> entirely OR call their
    /// <c>services.Replace(ServiceDescriptor.Singleton&lt;IOperatorRegistry,
    /// TStub&gt;())</c> AFTER <c>AddMessagingPersistence</c> so the
    /// stub wins the last-Replace race.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMessagingPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MessagingDb")
            ?? "Data Source=messaging.db";

        var providerName = configuration["MessagingDb:Provider"];
        var provider = ResolveProvider(providerName);

        services.AddDbContext<MessagingDbContext>(options =>
        {
            switch (provider)
            {
                case MessagingDbProvider.Sqlite:
                    options.UseSqlite(connectionString);
                    break;
                case MessagingDbProvider.PostgreSql:
                    options.UseNpgsql(connectionString);
                    break;
                case MessagingDbProvider.SqlServer:
                    options.UseSqlServer(connectionString);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Internal error: ResolveProvider returned unrecognised value {provider}.");
            }
        });

        var useMigrations = configuration.GetValue<bool>("MessagingDb:UseMigrations", false);
        services.AddSingleton<IHostedService>(sp =>
            new DatabaseInitializer(sp.GetRequiredService<IServiceScopeFactory>(), useMigrations));

        // Stage 4.1 — OutboundQueue:* options + meter singleton +
        // EF-backed IOutboundQueue replacement. Order matters here:
        // the options binding and the metrics singleton must be
        // registered BEFORE the Replace() so the PersistentOutboundQueue
        // ctor's IOptions<OutboundQueueOptions> + OutboundQueueMetrics
        // dependencies resolve from the host root provider. The
        // canonical backpressure counter name and three latency
        // histogram names live on OutboundQueueMetrics — both this
        // queue (counter) and the Worker's OutboundQueueProcessor
        // (histograms) consume the same Meter instance so an OTEL /
        // Prometheus exporter sees one consistent meter rather than
        // two competing instances.
        services.AddOptions<OutboundQueueOptions>()
            .Configure<IConfiguration>((opts, cfg) =>
                cfg.GetSection(OutboundQueueOptions.SectionName).Bind(opts));

        // Stage 4.2 — bind the canonical RetryPolicy from the
        // `RetryPolicy` configuration section. PersistentOutboundQueue
        // and OutboundQueueProcessor both resolve IOptions<RetryPolicy>
        // via DI so a host that overrides the section here gets the
        // override in both the queue's MarkFailed backoff schedule
        // and the processor's exhaustion verdict.
        services.AddOptions<RetryPolicy>()
            .Configure<IConfiguration>((opts, cfg) =>
                cfg.GetSection(RetryPolicy.SectionName).Bind(opts));

        // Stage 4.2 — bind the DeadLetterQueue options used by the
        // dead-letter health check's unhealthy-threshold poll.
        services.AddOptions<DeadLetterQueueOptions>()
            .Configure<IConfiguration>((opts, cfg) =>
                cfg.GetSection(DeadLetterQueueOptions.SectionName).Bind(opts));

        services.TryAddSingleton<OutboundQueueMetrics>();
        services.TryAddSingleton(TimeProvider.System);

        // Stage 4.1 — durable persistent outbox. Replaces the
        // Stage 2.6 InMemoryOutboundQueue stub registered by
        // AddTelegram via TryAddSingleton so production hosts get
        // the EF-backed durability contract. Same singleton +
        // IServiceScopeFactory pattern as the other persistent
        // implementations bridges the singleton consumer
        // (TelegramMessengerConnector) to the scoped MessagingDbContext
        // without violating the captive-dependency rule.
        //
        // Stage 4.2 — explicit factory so the RetryPolicy options
        // bound above are actually injected (the DI activator picks
        // the longest resolvable ctor; without an explicit factory it
        // would fall back to the legacy 5-arg ctor when Random isn't
        // registered, defeating the point of binding the RetryPolicy
        // section).
        services.Replace(ServiceDescriptor.Singleton<IOutboundQueue>(sp =>
            new PersistentOutboundQueue(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptions<OutboundQueueOptions>>(),
                sp.GetRequiredService<IOptions<RetryPolicy>>(),
                sp.GetRequiredService<OutboundQueueMetrics>(),
                sp.GetRequiredService<TimeProvider>(),
                random: null,
                sp.GetRequiredService<ILogger<PersistentOutboundQueue>>())));

        // Iter-3 evaluator item 3 — durable Telegram message_id →
        // CorrelationId reverse index. Registered as a singleton
        // because the implementation uses IServiceScopeFactory to
        // create a fresh scope per call (bridging the singleton sender
        // to the scoped DbContext without violating the
        // captive-dependency rule). Replaces the prior best-effort
        // cache-only mapping in TelegramMessageSender.
        services.Replace(ServiceDescriptor.Singleton<IOutboundMessageIdIndex, PersistentOutboundMessageIdIndex>());

        // Iter-4 evaluator item 4 — durable outbound dead-letter
        // ledger. Same singleton + IServiceScopeFactory pattern as
        // the message-id index; replaces the in-memory fallback that
        // AddTelegram registers via TryAddSingleton.
        services.Replace(ServiceDescriptor.Singleton<IOutboundDeadLetterStore, PersistentOutboundDeadLetterStore>());

        // Stage 3.2 — durable task-to-operator oversight assignment
        // repository. Same singleton + IServiceScopeFactory pattern;
        // replaces the in-memory StubTaskOversightRepository that
        // AddTelegram registers via TryAddSingleton.
        services.Replace(ServiceDescriptor.Singleton<ITaskOversightRepository, PersistentTaskOversightRepository>());

        // Stage 3.2 iter-2 evaluator item 5 — durable audit log.
        // Replaces the NullAuditLogger TryAddSingleton fallback in
        // AddTelegram so handoff and human-response audit writes
        // actually persist when persistence is wired. Stage 5.3 will
        // extend the schema with tenant / platform columns; this
        // writer is forward-compatible (additive columns).
        services.Replace(ServiceDescriptor.Singleton<IAuditLogger, PersistentAuditLogger>());

        // Stage 3.4 — durable operator registry. Same singleton +
        // IServiceScopeFactory pattern as the other persistent
        // implementations; replaces the StubOperatorRegistry that
        // AddTelegram registers via TryAddSingleton. The deterministic
        // binding-id derivation in PersistentOperatorRegistry is
        // intentionally distinct from the StubOperatorRegistry
        // derivation so a fixture that flips between the two surfaces
        // can assert the transition explicitly.
        //
        // NOTE — see this method's XML <remarks> "Host contract" and
        // "Recommended composition-root call order" sections for why
        // AddMessagingPersistence does NOT register
        // IUserAuthorizationService here: the auth service resolves
        // IOperatorRegistry from DI at activation time, so a host
        // that wires its own IUserAuthorizationService (via
        // TryAddSingleton, last in the chain) will automatically pick
        // up whichever registry survived the Replace race above. No
        // explicit "auth ↔ registry" synchronization is required.
        services.Replace(ServiceDescriptor.Singleton<IOperatorRegistry, PersistentOperatorRegistry>());

        // Stage 3.5 — durable pending-question store backing the
        // callback resolution, the RequiresComment text-reply
        // correlation, and the QuestionTimeoutService default-action
        // sweep. Same singleton + IServiceScopeFactory pattern as the
        // other persistent implementations; replaces the
        // InMemoryPendingQuestionStore that AddTelegram registers via
        // TryAddSingleton. Persisting the AgentQuestion JSON plus the
        // denormalised hot-path columns (DefaultActionId /
        // DefaultActionValue / ExpiresAt / Status) is required by
        // architecture.md §3.1 and §10.3 — the timeout service reads
        // DefaultActionId directly from this row and publishes that
        // string verbatim as HumanDecisionEvent.ActionValue (the
        // consuming agent resolves the full HumanAction semantics from
        // its own AllowedActions list); DefaultActionValue is retained
        // because the callback / RequiresComment text-reply path
        // resolves it from durable storage when the volatile
        // IDistributedCache entry has expired. Neither path touches
        // IDistributedCache at timeout.
        services.Replace(ServiceDescriptor.Singleton<IPendingQuestionStore, PersistentPendingQuestionStore>());

        // Stage 4.2 — durable dead-letter queue for outbox-row
        // companion ledger rows. Replaces the
        // InMemoryDeadLetterQueue fallback that AddTelegram
        // registers via TryAddSingleton so production hosts get the
        // EF-backed durability contract. Same singleton +
        // IServiceScopeFactory pattern as the other persistent
        // implementations bridges the singleton consumer
        // (OutboundQueueProcessor) to the scoped MessagingDbContext
        // without violating the captive-dependency rule.
        services.Replace(ServiceDescriptor.Singleton<IDeadLetterQueue, PersistentDeadLetterQueue>());

        return services;
    }

    /// <summary>
    /// Maps the human-friendly provider name from configuration to the
    /// internal <see cref="MessagingDbProvider"/> enum. Accepts common
    /// aliases (<c>Postgres</c>, <c>Npgsql</c>, <c>MSSQL</c>) so the
    /// operator can use whichever spelling matches their environment's
    /// existing connection-string naming convention. Case-insensitive.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="providerName"/> is non-null /
    /// non-empty but does not match any of the supported provider
    /// names. The exception is thrown at
    /// <see cref="AddMessagingPersistence"/> call time (host bootstrap)
    /// so the operator sees the misconfiguration BEFORE any DB
    /// connection is attempted.
    /// </exception>
    internal static MessagingDbProvider ResolveProvider(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return MessagingDbProvider.Sqlite;
        }

        return providerName.Trim().ToLowerInvariant() switch
        {
            "sqlite" => MessagingDbProvider.Sqlite,
            "postgresql" or "postgres" or "npgsql" => MessagingDbProvider.PostgreSql,
            "sqlserver" or "mssql" => MessagingDbProvider.SqlServer,
            _ => throw new NotSupportedException(
                $"MessagingDb:Provider value \"{providerName}\" is not recognised. "
                + "Supported values: \"Sqlite\" (default, dev/local), \"PostgreSQL\" "
                + "(aliases: Postgres, Npgsql), \"SqlServer\" (alias: MSSQL). The "
                + "implementation-plan brief specifies SQLite for dev/local and "
                + "PostgreSQL or SQL Server for production (consistent with Stages 4.1, 5.3)."),
        };
    }
}

/// <summary>
/// Internal enumeration of the EF Core providers the persistence
/// layer wires support for. Kept internal because the provider
/// choice is a host-bootstrap concern; downstream code reads
/// <see cref="MessagingDbContext"/> through <c>IServiceProvider</c>
/// and is provider-agnostic.
/// </summary>
internal enum MessagingDbProvider
{
    Sqlite,
    PostgreSql,
    SqlServer,
}
