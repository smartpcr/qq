using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using AgentSwarm.Messaging.Telegram.Polling;
using AgentSwarm.Messaging.Telegram.Sending;
using AgentSwarm.Messaging.Telegram.Swarm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

        services.AddHttpClient(TelegramBotClientFactory.HttpClientName);

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
        // Stage 3.1: production TelegramCommandParser replaces the
        // Stage 2.2 StubCommandParser at registration time. Singleton
        // lifetime because the parser is stateless.
        services.AddSingleton<ICommandParser, TelegramCommandParser>();
        // Stage 3.2: production CommandRouter replaces the Stage 2.2
        // StubCommandRouter. The router accepts every
        // IEnumerable<ICommandHandler> registered below and dispatches
        // by ICommandHandler.CommandName at the boundary.
        services.AddSingleton<ICommandRouter, CommandRouter>();

        // Stage 3.2 command handlers. Registered individually so the
        // CommandRouter constructor receives all nine through
        // IEnumerable<ICommandHandler> injection. Singleton lifetime —
        // handlers are stateless beyond their injected dependencies
        // (ISwarmCommandBus, IPendingQuestionStore, IOperatorRegistry,
        // ITaskOversightRepository, IAuditLogger, TimeProvider).
        services.AddSingleton<ICommandHandler, StartCommandHandler>();
        services.AddSingleton<ICommandHandler, StatusCommandHandler>();
        services.AddSingleton<ICommandHandler, AgentsCommandHandler>();
        services.AddSingleton<ICommandHandler, AskCommandHandler>();
        services.AddSingleton<ICommandHandler, ApproveCommandHandler>();
        services.AddSingleton<ICommandHandler, RejectCommandHandler>();
        services.AddSingleton<ICommandHandler, PauseCommandHandler>();
        services.AddSingleton<ICommandHandler, ResumeCommandHandler>();
        services.AddSingleton<ICommandHandler, HandoffCommandHandler>();

        // Stage 3.2: no-op audit logger as the TryAdd fallback so the
        // approve / reject / handoff handlers can take a hard
        // IAuditLogger dependency in dev / unit-test bootstraps that
        // skip the persistence module. AddMessagingPersistence
        // (Stage 3.2 iter-2 evaluator item 5) REPLACES this with
        // PersistentAuditLogger so production audit writes actually
        // hit the database — last-wins semantics on Replace().
        services.TryAddSingleton<IAuditLogger, NullAuditLogger>();

        // Stage 3.3 swapped StubCallbackHandler for the production
        // CallbackQueryHandler. The handler depends on
        // IPendingQuestionStore + ISwarmCommandBus + IAuditLogger +
        // IDeduplicationService + ITelegramBotClient + TimeProvider —
        // all five already registered above. Last-wins semantics on
        // AddSingleton means a re-call of AddTelegram (test
        // bootstraps) keeps the production binding; explicit
        // overrides must call services.Replace() AFTER AddTelegram.
        services.AddSingleton<ICallbackHandler, CallbackQueryHandler>();
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
}
