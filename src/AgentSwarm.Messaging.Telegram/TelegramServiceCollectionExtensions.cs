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
    /// <see cref="TelegramBotClientFactory"/>, a singleton
    /// <see cref="ITelegramBotClient"/> backed by the factory, the
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
    /// same instance.
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
        services.AddSingleton<ITelegramBotClient>(sp =>
            sp.GetRequiredService<TelegramBotClientFactory>().Create());

        // Stage 2.2 inbound pipeline + stubs. Stubs are singletons so their
        // in-memory state (dedup set, pending questions, pending workspace
        // disambiguations) survives across pipeline invocations within the
        // same process.
        services.AddSingleton<IDeduplicationService, InMemoryDeduplicationService>();
        services.AddSingleton<IPendingQuestionStore, InMemoryPendingQuestionStore>();
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

        services.AddSingleton<ICallbackHandler, StubCallbackHandler>();
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
