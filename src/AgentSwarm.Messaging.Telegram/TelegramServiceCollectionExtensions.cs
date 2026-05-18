using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using AgentSwarm.Messaging.Telegram.Diagnostics;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using AgentSwarm.Messaging.Telegram.Polling;
using AgentSwarm.Messaging.Telegram.Sending;
using AgentSwarm.Messaging.Telegram.Swarm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// DI registration extensions for the Telegram messenger connector.
/// </summary>
public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// Registers Telegram options binding (<see cref="TelegramOptions"/>),
    /// the fail-fast validator (<see cref="TelegramOptionsValidator"/>),
    /// the named <see cref="HttpClient"/>, the
    /// <see cref="TelegramBotClientFactory"/> (retained for direct
    /// one-shot construction sites), the Stage 5.1
    /// <see cref="RotatingTelegramBotClient"/> proxy, a singleton
    /// <see cref="ITelegramBotClient"/> resolved THROUGH the proxy so
    /// vault token rotations propagate on the next API call (per
    /// architecture.md §10 line 1018 and §11 line 1091), the
    /// Stage 2.2 <see cref="ITelegramUpdatePipeline"/>, the Stage 2.2
    /// in-memory stub implementations of the inbound pipeline's
    /// dependencies, and (Stage 2.5) the long-polling
    /// <see cref="TelegramPollingService"/> when
    /// <see cref="TelegramOptions.UsePolling"/> is <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Options are bound from the <c>Telegram</c> section of
    /// <paramref name="configuration"/>. <c>.ValidateOnStart()</c> wires
    /// the validator into <c>IHost.StartAsync</c> so a missing
    /// <see cref="TelegramOptions.BotToken"/> throws
    /// <see cref="OptionsValidationException"/> before the worker begins
    /// accepting traffic.
    /// </para>
    /// <para>
    /// The <see cref="ITelegramBotClient"/> registration is a singleton
    /// (the underlying <c>Telegram.Bot</c> client is thread-safe and
    /// reuses the <see cref="HttpClient"/>), so all senders share the
    /// same instance. The singleton is the
    /// <see cref="RotatingTelegramBotClient"/> proxy — every API call
    /// is forwarded to an inner <see cref="TelegramBotClient"/> that
    /// is rebuilt whenever <see cref="IOptionsMonitor{T}.OnChange"/>
    /// fires with a different <see cref="TelegramOptions.BotToken"/>.
    /// That is how a Key Vault rotation (driven by the
    /// <see cref="Azure.Extensions.AspNetCore.Configuration.Secrets.AzureKeyVaultConfigurationOptions.ReloadInterval"/>
    /// wired in <c>Program.cs</c>) reaches every cached
    /// <see cref="ITelegramBotClient"/> reference without a process
    /// restart, as required by architecture.md §11 line 1091
    /// ("The refreshed token is applied to the TelegramBotClient
    /// instance on the next API call").
    /// </para>
    /// <para>
    /// <b>Stage 2.2 stubs (with Stage 3.1 production swap).</b>
    /// <see cref="ICommandRouter"/>, <see cref="ICallbackHandler"/>,
    /// <see cref="IDeduplicationService"/>,
    /// <see cref="IPendingQuestionStore"/>, and
    /// <see cref="IPendingDisambiguationStore"/> are intentionally
    /// registered with their <i>stub</i> implementations here — they
    /// let the inbound pipeline run end-to-end before Phase 3
    /// (command processing, including the <c>CallbackQueryHandler</c>
    /// that consumes <see cref="IPendingDisambiguationStore.TakeAsync"/>
    /// for workspace disambiguation) and Phase 4 (deduplication)
    /// register the production replacements via additional
    /// <c>services.AddXxx()</c> calls. Re-registering an interface in
    /// a later phase replaces the stub via standard
    /// <see cref="IServiceCollection"/> last-wins semantics.
    /// </para>
    /// <para>
    /// <see cref="ICommandParser"/> is NO LONGER on the stub list —
    /// Stage 3.1 ships <see cref="Pipeline.TelegramCommandParser"/> as
    /// the production implementation, and the registration below
    /// points directly at it. The <see cref="Pipeline.Stubs.StubCommandParser"/>
    /// type still exists in the assembly but is no longer wired by
    /// <c>AddTelegram</c>; it is retained only as a reference shape
    /// (and to avoid breaking any third-party harness that may have
    /// instantiated it manually). Pinned by
    /// <c>TelegramPipelineRegistrationTests.AddTelegram_RegistersStage22Service</c>
    /// (the <c>ICommandParser → TelegramCommandParser</c> row) and the
    /// pipeline-level regression tests in
    /// <c>TelegramCommandParserTests</c>.
    /// </para>
    /// <para>
    /// <b><see cref="TimeProvider"/>.</b> Registered via
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection, TService)"/>
    /// so tests can pre-register a fake <see cref="TimeProvider"/>
    /// (used by <see cref="TelegramUpdatePipeline"/> to compute the
    /// <see cref="PendingDisambiguation.ExpiresAt"/> TTL deterministically)
    /// without losing to the production default of
    /// <see cref="TimeProvider.System"/>.
    /// </para>
    /// <para>
    /// <b>Authorization service is NOT stubbed.</b>
    /// <c>IUserAuthorizationService</c> (in the Core project) is a Phase 4
    /// concern and is registered separately. Resolving
    /// <see cref="ITelegramUpdatePipeline"/> before that registration
    /// will fail at first activation; this is intentional so missing
    /// authorization is a loud bootstrap failure rather than a silent
    /// allow-everything stub.
    /// </para>
    /// </remarks>
    /// <param name="services">DI container.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddTelegram(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>();

        // Stage 6.1 -- the Telegram Bot API embeds the bearer bot
        // token in the URL path (`/bot{TOKEN}/sendMessage`). The
        // default Microsoft.Extensions.Http logging handlers
        // (LoggingHttpMessageHandler + LoggingScopeHttpMessageHandler)
        // log "Start processing HTTP request {Method} {Uri}" at
        // Information level and would write that token verbatim to
        // every operator's logs -- a direct violation of the brief's
        // "Token excluded from logs" acceptance scenario.
        //
        // RemoveAllLoggers() strips BOTH default handlers; AddLogger
        // attaches our RedactingHttpClientLogger which mirrors the
        // start/stop/failed log shape but runs every URL through
        // TelegramHttpRedactor.Redact before formatting. The pairing
        // is load-bearing: without RemoveAllLoggers first, the
        // defaults would still emit the raw URL alongside our
        // redacted line.
        //
        // AddLogger<TLogger> resolves TLogger via the service
        // provider on every HTTP message handler build, so the
        // logger MUST be registered separately. Transient mirrors the
        // lifetime contract of the default Microsoft.Extensions.Http
        // logging handlers (one per pipeline build); the logger has
        // no per-request state so the choice is safe.
        services.TryAddTransient<RedactingHttpClientLogger>();
        services.AddHttpClient(TelegramBotClientFactory.HttpClientName)
            .RemoveAllLoggers()
            .AddLogger<RedactingHttpClientLogger>();

        services.AddSingleton<TelegramBotClientFactory>();

        // Stage 5.1 secret-rotation wiring. The proxy is registered as
        // its own concrete singleton so a future test or diagnostic
        // can resolve it directly, and as the ITelegramBotClient
        // singleton so every existing consumer
        // (TelegramMessageSender, TelegramBotClientUpdatePoller,
        // CallbackQueryHandler, QuestionTimeoutService, the webhook
        // registration service, the polling service) automatically
        // observes vault rotations on the next API call. The proxy
        // ctor subscribes to IOptionsMonitor<TelegramOptions>.OnChange
        // — that is the contract architecture.md §11 line 1091 calls
        // out ("the refreshed token is applied to the
        // TelegramBotClient instance on the next API call"). Without
        // this indirection the singleton ITelegramBotClient would
        // capture the FIRST-seen token at activation time and ignore
        // every subsequent vault refresh until process restart.
        services.AddSingleton<RotatingTelegramBotClient>();
        services.AddSingleton<ITelegramBotClient>(sp =>
            sp.GetRequiredService<RotatingTelegramBotClient>());

        // Stage 4.3 — IDeduplicationService.
        //
        // The dev / local backend is the in-memory sliding-window
        // implementation (ConcurrentDictionary<string, DateTimeOffset>
        // + periodic cleanup timer) per implementation-plan.md
        // Stage 4.3 step 2. Registered via TryAddSingleton so a host
        // that ALSO wires AddMessagingPersistence wins last-Replace
        // and gets the EF-backed PersistentDeduplicationService instead
        // — without that ordering guarantee the in-memory backend
        // would silently shadow the persistent store in production
        // (the AddTelegram → AddMessagingPersistence composition path
        // documented in the Worker's Program.cs).
        //
        // The SlidingWindowDeduplicationService ctor takes
        // IOptions<DeduplicationOptions>, TimeProvider, and
        // ILogger<SlidingWindowDeduplicationService>. The options
        // binding and TimeProvider registration below are
        // TryAdd-guarded so AddMessagingPersistence's own binding
        // (and a host that registered an alternate TimeProvider for
        // testing) still wins.
        services.AddOptions<DeduplicationOptions>()
            .Configure(opts => configuration.GetSection(DeduplicationOptions.SectionName).Bind(opts));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDeduplicationService, SlidingWindowDeduplicationService>();

        // Stage 3.5 — IPendingQuestionStore uses TryAddSingleton so a
        // host that already wired AddMessagingPersistence (which calls
        // services.Replace<IPendingQuestionStore, PersistentPendingQuestionStore>())
        // keeps the persistent EF-backed implementation. Plain
        // AddSingleton here would append a second descriptor that the
        // default ServiceProvider would resolve last-wins, silently
        // overriding the persistent store with the in-memory stub. The
        // other replaceable abstractions (IAuditLogger, IOperatorRegistry,
        // IOutboundDeadLetterStore, IOutboundMessageIdIndex,
        // ITaskOversightRepository) already use TryAddSingleton for the
        // same reason.
        services.TryAddSingleton<IPendingQuestionStore, InMemoryPendingQuestionStore>();

        services.AddSingleton<IPendingDisambiguationStore, InMemoryPendingDisambiguationStore>();

        // Stage 6.3 iter-3 evaluator item 1 — the Stage 3.1/3.2/3.3
        // command-processing surface (ICommandParser, ICommandRouter,
        // all nine ICommandHandler implementations, ICallbackHandler)
        // is delegated to the public AddCommandProcessing() extension
        // method so the Worker host (Program.cs) can call it
        // explicitly per the Stage 6.3 brief. AddCommandProcessing()
        // is idempotent (TryAddSingleton + TryAddEnumerable), so
        // re-invoking it from the Worker after AddTelegram is a
        // no-op that satisfies the brief's "call services.AddCommandProcessing()"
        // requirement without producing duplicate registrations.
        services.AddCommandProcessing();

        // Stage 3.2: no-op audit logger as the TryAdd fallback so the
        // approve / reject / handoff handlers can take a hard
        // IAuditLogger dependency in dev / unit-test bootstraps that
        // skip the persistence module. AddMessagingPersistence
        // (Stage 3.2 iter-2 evaluator item 5) REPLACES this with
        // PersistentAuditLogger so production audit writes actually
        // hit the database — last-wins semantics on Replace().
        services.TryAddSingleton<IAuditLogger, NullAuditLogger>();

        // Stage 5.3 iter-9 evaluator item 2 — no-op fallback sink
        // as the TryAdd default so TelegramUpdatePipeline can take a
        // hard IAuditFallbackSink dependency in dev / unit-test
        // bootstraps that skip the persistence module.
        // AddMessagingPersistence REPLACES this with the file-backed
        // FileAuditFallbackSink so production hosts get a durable
        // backstop when the primary audit DB throws — last-wins
        // semantics on Replace(). Pairing PersistentAuditLogger with
        // the no-op fallback would silently violate Stage 5.3's
        // "log every inbound command" contract on any audit-DB
        // outage, which is exactly why AddMessagingPersistence
        // always replaces this binding.
        services.TryAddSingleton<IAuditFallbackSink, NullAuditFallbackSink>();

        // Stage 3.3 swapped StubCallbackHandler for the production
        // CallbackQueryHandler. The handler depends on
        // IPendingQuestionStore + ISwarmCommandBus + IAuditLogger +
        // IDeduplicationService + ITelegramBotClient + TimeProvider —
        // all five already registered above. The actual registration
        // now lives in AddCommandProcessing() (called from this
        // method's body above and re-callable from Program.cs); the
        // line below is intentionally REMOVED to avoid a duplicate
        // descriptor that would otherwise leak into IEnumerable<ICallbackHandler>
        // resolutions.
        // TimeProvider.System is the production default; tests register a
        // FakeTimeProvider via TryAddSingleton-replacement before AddTelegram.
        services.TryAddSingleton(TimeProvider.System);

        // Stage 2.6: unbounded in-process bridge channel between the
        // inbound pipeline (writer) and the IMessengerConnector.ReceiveAsync
        // drain (reader). The backing channel is constructed via
        // Channel.CreateUnbounded<MessengerEvent> (see
        // ProcessedMessengerEventChannel.ctor) so every processed update
        // reaches the connector drain losslessly — the Stage 2.6
        // "burst from 100+ agents without message loss" SLO forbids any
        // bounded / fast-drop shape on this hop, and unlike the
        // Webhook/InboundUpdateChannel there is no durable InboundUpdate
        // backstop row here for replay. Registered as a singleton so
        // the producer and consumer share ONE in-process buffer, and
        // registered BEFORE the pipeline so the pipeline's
        // [ActivatorUtilitiesConstructor] overload can resolve it as
        // a constructor argument.
        services.TryAddSingleton<ProcessedMessengerEventChannel>();

        // Stage 2.6: stub IOutboundQueue so TelegramMessengerConnector's
        // dependency is satisfiable before Stage 4.1 ships the durable
        // persistent queue. TryAddSingleton — the Stage 4.1 production
        // registration (AddSingleton<IOutboundQueue, PersistentOutboundQueue>)
        // wins by last-wins semantics. Mirrors the existing
        // InMemoryDeduplicationService / InMemoryOutboundMessageIdIndex /
        // InMemoryOutboundDeadLetterStore replacement pattern.
        services.TryAddSingleton<IOutboundQueue, InMemoryOutboundQueue>();

        // Stage 4.2 — dev fallback for the outbox-row companion
        // dead-letter queue. Same TryAdd-fallback + production-replace
        // pattern as IOutboundQueue / IOutboundDeadLetterStore /
        // IOutboundMessageIdIndex: the persistence module's
        // PersistentDeadLetterQueue replaces this registration via
        // AddMessagingPersistence's Replace() call so production hosts
        // get the EF-backed durability contract.
        services.TryAddSingleton<IDeadLetterQueue, InMemoryDeadLetterQueue>();

        services.AddSingleton<ITelegramUpdatePipeline, TelegramUpdatePipeline>();

        // Stage 2.5: long-polling receiver (development mode).
        //
        // The poller abstraction is always registered so tests can resolve
        // it without conditioning on configuration; the hosted service is
        // only added when Telegram:UsePolling=true. The mutual-exclusion
        // guard between polling and webhook modes lives in
        // TelegramOptionsValidator (runs at host startup via
        // ValidateOnStart) — keeping it there means the conflict is
        // surfaced even when callers register the polling service
        // manually and bypass this extension.
        //
        // Configuration is read here (synchronously, off the supplied
        // IConfiguration) rather than via IOptionsMonitor at runtime
        // because hosted-service registration is one-shot: changing
        // UsePolling after the host has started has no effect on the
        // hosted-service set, so reading the binding once at registration
        // time is the canonical pattern.
        services.AddSingleton<ITelegramUpdatePoller, TelegramBotClientUpdatePoller>();

        // Stage 2.3: outbound sender + dual-layer token-bucket rate limiter
        // + IDistributedCache for HumanAction lookups (see
        // TelegramQuestionRenderer / TelegramMessageSender). The sender is
        // a singleton because it is stateless beyond its injected
        // dependencies; the limiter is a singleton so its per-chat token
        // buckets survive across worker invocations within the same
        // process. AddDistributedMemoryCache is idempotent via the
        // TryAdd-based implementation inside the framework, so re-calling
        // it from a higher layer (Worker host) does not double-register.
        services.AddDistributedMemoryCache();
        services.TryAddSingleton<ITelegramRateLimiter, TokenBucketTelegramRateLimiter>();
        // Iter-3 evaluator item 3 — the sender depends on a durable
        // message-id → CorrelationId index. The Telegram extension
        // registers an InMemoryOutboundMessageIdIndex as the
        // TryAddSingleton fallback so dev / unit-test bootstraps that
        // skip the persistence module can still resolve the sender.
        // Production replaces this registration with the EF-backed
        // PersistentOutboundMessageIdIndex via
        // AddMessagingPersistence's Replace() call.
        services.TryAddSingleton<IOutboundMessageIdIndex, InMemoryOutboundMessageIdIndex>();
        // Iter-4 evaluator item 4 — the sender also depends on a
        // durable dead-letter ledger so retry-exhausted sends are
        // observable in the database. Same TryAdd fallback +
        // Replace pattern as the msg-id index.
        services.TryAddSingleton<IOutboundDeadLetterStore, InMemoryOutboundDeadLetterStore>();
        services.TryAddSingleton<IMessageSender, TelegramMessageSender>();

        // Stage 2.6: the connector is the platform-agnostic facade the
        // agent swarm uses to send messages / questions and to drain
        // processed inbound events. Singleton lifetime — the type is
        // stateless beyond its singleton dependencies (IOutboundQueue,
        // ProcessedMessengerEventChannel, TimeProvider, ILogger). Concrete
        // type is also registered so tests / diagnostics can resolve the
        // implementation without going through the interface.
        services.TryAddSingleton<TelegramMessengerConnector>();
        services.TryAddSingleton<IMessengerConnector>(sp =>
            sp.GetRequiredService<TelegramMessengerConnector>());

        // Stage 2.7: Swarm Event Ingress Service stubs + hosted service.
        //
        // The three stubs (StubOperatorRegistry, StubTaskOversightRepository,
        // StubSwarmCommandBus) are registered via TryAddSingleton so the
        // production replacements (Stage 3.4 PersistentOperatorRegistry,
        // Stage 3.2 PersistentTaskOversightRepository, and the concrete
        // swarm transport adapter — out of scope for this story) win by
        // AddSingleton last-wins semantics. A Phase 6.3 startup health
        // check is required to assert that the resolved types are NOT
        // the stubs when ASPNETCORE_ENVIRONMENT=Production.
        //
        // SwarmEventSubscriptionService runs as a hosted background
        // service that, on startup, calls IOperatorRegistry.GetActiveTenantsAsync
        // and opens one ISwarmCommandBus.SubscribeAsync stream per tenant.
        // Events are routed through the connector — questions via
        // SendQuestionAsync, alerts/status via SendMessageAsync — per
        // implementation-plan.md Stage 2.7.
        services.TryAddSingleton<IOperatorRegistry, StubOperatorRegistry>();
        services.TryAddSingleton<ITaskOversightRepository, StubTaskOversightRepository>();
        services.TryAddSingleton<ISwarmCommandBus, StubSwarmCommandBus>();
        services.AddSingleton<SwarmEventSubscriptionService>();
        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredService<SwarmEventSubscriptionService>());

        // Stage 3.5 — pending-question timeout sweeper. Polls
        // IPendingQuestionStore.GetExpiredAsync, atomically claims
        // each expired row via MarkTimedOutAsync, then publishes a
        // HumanDecisionEvent whose ActionValue is the
        // PendingQuestionRecord.DefaultActionId string verbatim (the
        // consuming agent resolves the full HumanAction.Value
        // semantics from its own AllowedActions list per
        // architecture.md §10.3) — when DefaultActionId is null the
        // service falls back to the "__timeout__" sentinel. The
        // sweeper deliberately does NOT read
        // PendingQuestionRecord.DefaultActionValue (that column is
        // owned by the callback / RequiresComment text-reply path's
        // cache-miss fallback — §5.2 invariant 3). On publish failure
        // the claim is reverted via TryRevertTimedOutClaimAsync so
        // the next sweep retries (at-least-once delivery). The
        // service then edits the original Telegram message, writes a
        // HumanResponseAuditEntry, and leaves the row TimedOut.
        // Options are bound from Telegram:QuestionTimeout —
        // registered with default PollInterval=30s if the section is
        // missing.
        services.AddOptions<QuestionTimeoutOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName)
                .GetSection(QuestionTimeoutOptions.SectionName));
        services.AddSingleton<QuestionTimeoutService>();
        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredService<QuestionTimeoutService>());

        var pollingSnapshot = configuration
            .GetSection(TelegramOptions.SectionName)
            .Get<TelegramOptions>();
        if (pollingSnapshot is not null && pollingSnapshot.UsePolling)
        {
            // Defense-in-depth: even though the validator already rejects
            // UsePolling=true + WebhookUrl set, refuse to register the
            // hosted service when both are configured so a developer who
            // disables ValidateOnStart cannot accidentally run both
            // receivers in parallel. The validator path is still the
            // canonical "fail at host startup" mechanism — see
            // TelegramOptionsValidator.Validate.
            if (!string.IsNullOrWhiteSpace(pollingSnapshot.WebhookUrl))
            {
                throw new InvalidOperationException(
                    "Telegram:UsePolling and Telegram:WebhookUrl are mutually exclusive. "
                    + "Refusing to register TelegramPollingService while WebhookUrl is set. "
                    + "See TelegramOptionsValidator for the host-startup version of this guard.");
            }

            services.AddSingleton<TelegramPollingService>();
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<TelegramPollingService>());
        }

        return services;
    }

    /// <summary>
    /// Stage 6.3 — explicit in-memory <see cref="IOutboundQueue"/>
    /// composition switch for dev / local hosts. The Worker's
    /// production composition wires
    /// <c>AddMessagingPersistence(...)</c> BEFORE <c>AddTelegram(...)</c>,
    /// which <see cref="ServiceCollectionDescriptorExtensions.Replace(IServiceCollection, ServiceDescriptor)"/>s
    /// <see cref="IOutboundQueue"/> with the EF-backed
    /// <c>PersistentOutboundQueue</c>. Hosts that want the
    /// brief-mandated "in-memory queue" for
    /// <c>appsettings.Development.json</c> call this method AFTER
    /// the persistence + Telegram registrations so the
    /// last-Replace-wins semantics swap the durable queue back to
    /// the in-process <see cref="InMemoryOutboundQueue"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief (Stage 6.3, second-to-last bullet) explicitly
    /// requires <c>appsettings.Development.json</c> to select the
    /// in-memory queue. The dev queue lives behind the
    /// <c>internal</c> visibility of
    /// <see cref="InMemoryOutboundQueue"/>; this extension is the
    /// public composition surface that lets the Worker
    /// (which lives in a sibling assembly without
    /// <c>InternalsVisibleTo</c>) make the swap without leaking
    /// the dev type itself.
    /// </para>
    /// <para>
    /// <b>Side-effect-free for hosts that do not call it.</b>
    /// Production hosts (and any host that leaves
    /// <c>OutboundQueue:Mode</c> unset or set to
    /// <c>Persistent</c>) never invoke this method, so the
    /// <see cref="ServiceCollectionDescriptorExtensions.Replace(IServiceCollection, ServiceDescriptor)"/>
    /// call here cannot accidentally regress the durable outbox.
    /// </para>
    /// </remarks>
    /// <param name="services">The DI container being composed.</param>
    /// <returns>
    /// The same <paramref name="services"/> instance for chaining.
    /// </returns>
    public static IServiceCollection UseInMemoryOutboundQueue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Replace (not TryAdd) — at this composition order
        // AddMessagingPersistence has already registered the
        // PersistentOutboundQueue singleton; only Replace will
        // unseat it.
        services.Replace(ServiceDescriptor.Singleton<IOutboundQueue, InMemoryOutboundQueue>());
        return services;
    }

    /// <summary>
    /// Stage 6.3 (iter-3 evaluator item 1) — public composition
    /// seam for the Telegram command-processing surface. Registers
    /// <see cref="ICommandParser"/>, <see cref="ICommandRouter"/>,
    /// every <see cref="ICommandHandler"/> implementation for the
    /// nine supported commands (<c>/start</c>, <c>/status</c>,
    /// <c>/agents</c>, <c>/ask</c>, <c>/approve</c>, <c>/reject</c>,
    /// <c>/pause</c>, <c>/resume</c>, <c>/handoff</c>), and the
    /// <see cref="ICallbackHandler"/> that consumes inline-keyboard
    /// button presses. The Stage 6.3 brief explicitly names this
    /// extension as a Worker-host composition surface
    /// (<c>services.AddCommandProcessing()</c>), so this method is
    /// callable from <c>Program.cs</c> alongside
    /// <see cref="AddTelegram(IServiceCollection, IConfiguration)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotency.</b> All registrations use
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService,TImplementation}(IServiceCollection)"/>
    /// and
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, IEnumerable{ServiceDescriptor})"/>
    /// so calling this method multiple times — for example, once
    /// indirectly via <see cref="AddTelegram(IServiceCollection, IConfiguration)"/>
    /// and once explicitly from the Worker host — produces no
    /// duplicate descriptors. Each (service-type,
    /// implementation-type) pair is added exactly once across all
    /// calls.
    /// </para>
    /// <para>
    /// <b>Why this is a separate method from
    /// <see cref="AddTelegram(IServiceCollection, IConfiguration)"/>.</b>
    /// The Stage 6.3 brief explicitly enumerates the
    /// command-processing registrations as a discrete composition
    /// surface so a Worker host can opt into command processing
    /// independent of the inbound pipeline / outbound queue /
    /// hosted services that <c>AddTelegram</c> bundles. For
    /// example, a unit-test bootstrap that wants to exercise
    /// <c>ICommandRouter</c> in isolation calls only
    /// <c>AddCommandProcessing()</c> without paying the
    /// configuration-binding cost of <c>AddTelegram</c>.
    /// </para>
    /// <para>
    /// <b>Composition order.</b> Handlers depend on
    /// <see cref="ISwarmCommandBus"/>, <see cref="IPendingQuestionStore"/>,
    /// <see cref="IOperatorRegistry"/>,
    /// <see cref="ITaskOversightRepository"/>, and
    /// <see cref="IAuditLogger"/>. Those abstractions are
    /// registered by <see cref="AddTelegram(IServiceCollection, IConfiguration)"/>
    /// (with stub fallbacks via TryAddSingleton, replaced by
    /// production siblings via AddMessagingPersistence). When this
    /// method is called from a host that has not also called
    /// <c>AddTelegram</c> and <c>AddMessagingPersistence</c>, the
    /// caller is responsible for supplying those abstractions
    /// before the container is built.
    /// </para>
    /// </remarks>
    /// <param name="services">The DI container being composed.</param>
    /// <returns>
    /// The same <paramref name="services"/> instance for chaining.
    /// </returns>
    public static IServiceCollection AddCommandProcessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Stage 3.1 production TelegramCommandParser replaces the
        // Stage 2.2 StubCommandParser at registration time.
        // TryAddSingleton — tests/hosts that pre-register an
        // ICommandParser before calling AddCommandProcessing win;
        // the production default is preserved otherwise.
        services.TryAddSingleton<ICommandParser, TelegramCommandParser>();

        // Stage 3.2 production CommandRouter replaces the Stage 2.2
        // StubCommandRouter. The router accepts every
        // IEnumerable<ICommandHandler> registered below and
        // dispatches by ICommandHandler.CommandName at the boundary.
        services.TryAddSingleton<ICommandRouter, CommandRouter>();

        // Stage 3.2 command handlers. TryAddEnumerable adds each
        // (service-type, implementation-type) pair exactly once
        // across all calls, so re-invocation from the Worker host
        // does not produce a duplicate handler that the router
        // would dispatch twice for a single command.
        services.TryAddEnumerable(new[]
        {
            ServiceDescriptor.Singleton<ICommandHandler, StartCommandHandler>(),
            ServiceDescriptor.Singleton<ICommandHandler, StatusCommandHandler>(),
            ServiceDescriptor.Singleton<ICommandHandler, AgentsCommandHandler>(),
            ServiceDescriptor.Singleton<ICommandHandler, AskCommandHandler>(),
            ServiceDescriptor.Singleton<ICommandHandler, ApproveCommandHandler>(),
            ServiceDescriptor.Singleton<ICommandHandler, RejectCommandHandler>(),
            ServiceDescriptor.Singleton<ICommandHandler, PauseCommandHandler>(),
            ServiceDescriptor.Singleton<ICommandHandler, ResumeCommandHandler>(),
            ServiceDescriptor.Singleton<ICommandHandler, HandoffCommandHandler>(),
        });

        // Stage 3.3 production CallbackQueryHandler. Wired through
        // ICallbackHandler so the inbound pipeline (which depends
        // on the interface, not the concrete type) can resolve it.
        services.TryAddSingleton<ICallbackHandler, CallbackQueryHandler>();

        return services;
    }
}
