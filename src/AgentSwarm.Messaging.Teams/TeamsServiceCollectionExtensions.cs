using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Commands;
using AgentSwarm.Messaging.Teams.Extensions;
using AgentSwarm.Messaging.Teams.Lifecycle;
using AgentSwarm.Messaging.Teams.Outbox;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// DI registration helpers for the Teams messenger connector. Implements the wiring step
/// from <c>implementation-plan.md</c> §2.3 step 5 ("Wire <c>TeamsMessengerConnector</c> into
/// DI as <c>IMessengerConnector</c> keyed by <c>"teams"</c>").
/// </summary>
/// <remarks>
/// <para>
/// The wiring is intentionally split into two narrow helpers so a host can compose them
/// independently:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="AddInProcessInboundEventChannel"/> — registers a singleton
/// <see cref="ChannelInboundEventPublisher"/> instance under both
/// <see cref="IInboundEventPublisher"/> and <see cref="IInboundEventReader"/>. The same
/// instance backs both interfaces so the writer used by inbound producers and the reader
/// used by <see cref="TeamsMessengerConnector"/> share the same channel.
/// </description></item>
/// <item><description>
/// <see cref="AddTeamsMessengerConnector"/> — registers
/// <see cref="TeamsMessengerConnector"/> as a singleton concrete type and exposes the same
/// instance under the <see cref="IMessengerConnector"/> service type keyed by
/// <c>"teams"</c> using the .NET 8 keyed-services API. Hosts that need the connector also
/// as <c>ITeamsCardManager</c> in Stage 3.3 can layer a second
/// <c>AddSingleton&lt;ITeamsCardManager&gt;</c> registration on the same singleton.
/// Also fills in default registrations for two collaborators required by
/// <see cref="TeamsMessengerConnector"/>'s <b>nine</b>-argument constructor:
/// <list type="bullet">
/// <item><description>An <see cref="IConversationReferenceRouter"/> that adapts the
/// registered <see cref="IConversationReferenceStore"/> singleton when that store also
/// implements <see cref="IConversationReferenceRouter"/> — the canonical Stage 2.1
/// in-memory store and the Stage 4.1 SQL store both do, so a single store
/// registration satisfies both interfaces in production DI.</description></item>
/// <item><description>An <see cref="Cards.IAdaptiveCardRenderer"/> backed by the
/// canonical <see cref="Cards.AdaptiveCardBuilder"/> implementation (Stage 3.1) so
/// <see cref="TeamsMessengerConnector.SendQuestionAsync"/> can render proactive
/// Adaptive Card questions.</description></item>
/// </list>
/// Hosts that ship custom router or renderer implementations can register them BEFORE
/// calling this helper; the <c>TryAdd*</c> idempotency guards leave explicit
/// registrations untouched.
/// </description></item>
/// </list>
/// <para>
/// Both helpers are <b>idempotent</b>: every registration uses
/// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton(IServiceCollection, Type, Type)"/>
/// (or the keyed/factory variants), so calling either method more than once does not
/// stack duplicate descriptors. The second call observes the existing registration and
/// becomes a no-op for that service type.
/// </para>
/// </remarks>
public static class TeamsServiceCollectionExtensions
{
    /// <summary>
    /// Canonical key under which <see cref="TeamsMessengerConnector"/> is registered as
    /// <see cref="IMessengerConnector"/> for keyed-service resolution.
    /// </summary>
    public const string MessengerKey = "teams";

    /// <summary>
    /// Register a single <see cref="ChannelInboundEventPublisher"/> instance and expose it
    /// under both <see cref="IInboundEventPublisher"/> and <see cref="IInboundEventReader"/>.
    /// Idempotent — calling this method multiple times leaves the descriptor count
    /// unchanged for each of the three service types because every registration uses
    /// <c>TryAddSingleton</c>.
    /// </summary>
    public static IServiceCollection AddInProcessInboundEventChannel(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.TryAddSingleton<ChannelInboundEventPublisher>();
        services.TryAddSingleton<IInboundEventPublisher>(sp => sp.GetRequiredService<ChannelInboundEventPublisher>());
        services.TryAddSingleton<IInboundEventReader>(sp => sp.GetRequiredService<ChannelInboundEventPublisher>());
        return services;
    }

    /// <summary>
    /// Register <see cref="TeamsMessengerConnector"/> as a singleton and expose it as
    /// <see cref="IMessengerConnector"/> keyed by <see cref="MessengerKey"/> (.NET 8 keyed
    /// services). Also wires the in-process inbound event channel via
    /// <see cref="AddInProcessInboundEventChannel"/> so callers do not have to remember to
    /// call both helpers in order. The helper additionally fills in a default
    /// <see cref="IConversationReferenceRouter"/> registration that adapts the host-supplied
    /// <see cref="IConversationReferenceStore"/> singleton — see
    /// <see cref="IConversationReferenceRouter"/> for the contract documenting that the
    /// canonical store implementations satisfy both interfaces. Idempotent — every
    /// registration uses <c>TryAdd*</c> variants so repeated calls do not stack
    /// duplicates and explicit pre-registrations of any of the affected service types are
    /// preserved.
    /// </summary>
    public static IServiceCollection AddTeamsMessengerConnector(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddInProcessInboundEventChannel();
        services.AddTeamsCommandDispatcher();

        // Stage 5.1 — every host that wires the Teams connector inherits the security
        // graph by default. Without this, hosts that compose AddTeamsMessengerConnector
        // alone (omitting an explicit AddTeamsSecurity() call) silently run with the
        // Stage 2.1 default-deny stubs for IIdentityResolver / IUserAuthorizationService,
        // and the TenantValidationMiddleware / InstallationStateGate /
        // TeamsAppPolicyHealthCheck are never registered. Idempotent — AddTeamsSecurity
        // uses RemoveAll + TryAdd so the second invocation is a no-op for callers that
        // already wired the security graph explicitly.
        services.AddTeamsSecurity();

        // Stage 5.1 iter-4 evaluator feedback item 2 — TimeProvider MUST be in the DI graph
        // so the constructor-with-TimeProvider+InstallationStateGate overload of
        // TeamsMessengerConnector / TeamsProactiveNotifier is the one DI resolves.
        // Without this, the .NET DI activator falls back to a shorter constructor that
        // delegates with installationStateGate: null, silently bypassing the install-state
        // pre-check in production.
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        services.TryAddSingleton<IMessageExtensionHandler, MessageExtensionHandler>();

        // Stage 5.1 iter-4 evaluator feedback item 2 — pin the connector registration to a
        // factory that explicitly resolves the canonical 10-arg constructor (which carries
        // the InstallationStateGate). Relying on .NET DI's "longest satisfiable constructor"
        // heuristic is too fragile: if a host registers TimeProvider but not
        // InstallationStateGate the activator silently picks the 9-arg overload and the
        // install-state pre-check never runs. The factory below resolves the gate via
        // GetRequiredService<InstallationStateGate>(), so any mis-wiring fails LOUDLY at
        // first connector resolution rather than silently dropping security enforcement.
        services.TryAddSingleton<TeamsMessengerConnector>(sp => new TeamsMessengerConnector(
            adapter: sp.GetRequiredService<Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter>(),
            options: sp.GetRequiredService<TeamsMessagingOptions>(),
            conversationReferenceStore: sp.GetRequiredService<IConversationReferenceStore>(),
            conversationReferenceRouter: sp.GetRequiredService<IConversationReferenceRouter>(),
            agentQuestionStore: sp.GetRequiredService<IAgentQuestionStore>(),
            cardStateStore: sp.GetRequiredService<ICardStateStore>(),
            cardRenderer: sp.GetRequiredService<IAdaptiveCardRenderer>(),
            inboundEventReader: sp.GetRequiredService<IInboundEventReader>(),
            logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TeamsMessengerConnector>>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            installationStateGate: sp.GetRequiredService<InstallationStateGate>()));

        services.TryAddKeyedSingleton<IMessengerConnector>(
            MessengerKey,
            (sp, _) => sp.GetRequiredService<TeamsMessengerConnector>());

        // Default IAdaptiveCardRenderer wiring per implementation-plan §3.1 step 7 and the
        // architecture.md §4.6 cross-doc note: the canonical concrete is
        // AdaptiveCardBuilder; the contract surface is IAdaptiveCardRenderer. Hosts that
        // ship a custom renderer can register it BEFORE calling this helper —
        // TryAddSingleton leaves the explicit registration untouched.
        services.TryAddSingleton<IAdaptiveCardRenderer, AdaptiveCardBuilder>();

        // Default IConversationReferenceRouter wiring — adapt the host-supplied
        // IConversationReferenceStore by exposing it under the companion interface as well.
        // The canonical store implementations (Stage 2.1 InMemoryConversationReferenceStore
        // and Stage 4.1 SqlConversationReferenceStore) both implement BOTH interfaces, so
        // this cast-based adapter resolves to the same singleton without an extra type.
        // Hosts that ship a router-only implementation can register it BEFORE calling this
        // helper; TryAddSingleton leaves the explicit registration untouched. Hosts that
        // wire a store NOT implementing IConversationReferenceRouter get a clear startup
        // error at first connector resolution rather than a NullReferenceException deep
        // inside SendMessageAsync.
        services.TryAddSingleton<IConversationReferenceRouter>(sp =>
        {
            var store = sp.GetRequiredService<IConversationReferenceStore>();
            return store as IConversationReferenceRouter
                ?? throw new InvalidOperationException(
                    $"The registered IConversationReferenceStore implementation " +
                    $"'{store.GetType().FullName}' does not also implement " +
                    "IConversationReferenceRouter. TeamsMessengerConnector.SendMessageAsync " +
                    "needs the companion router contract to resolve a stored conversation " +
                    "reference from a bare ConversationId (MessengerMessage carries no " +
                    "TenantId). The canonical store implementations — Stage 2.1's " +
                    "InMemoryConversationReferenceStore and Stage 4.1's " +
                    "SqlConversationReferenceStore — implement BOTH interfaces. To use a " +
                    "store that implements only IConversationReferenceStore, register a " +
                    "separate IConversationReferenceRouter implementation BEFORE calling " +
                    "AddTeamsMessengerConnector (the TryAddSingleton in this helper will " +
                    "then leave your registration in place).");
        });

        return services;
    }

    /// <summary>
    /// Register the Stage 3.2 <see cref="CommandDispatcher"/> and every concrete
    /// <see cref="ICommandHandler"/> shipped in <see cref="AgentSwarm.Messaging.Teams.Commands"/>
    /// (Ask, Status, Approve, Reject, Escalate, Pause, Resume). Idempotent — every
    /// registration uses <c>TryAdd*</c> so calling this helper multiple times leaves the
    /// descriptor count unchanged. The dispatcher itself is exposed under
    /// <see cref="ICommandDispatcher"/> (the contract consumed by
    /// <see cref="TeamsSwarmActivityHandler"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default <see cref="IAdaptiveCardRenderer"/> registration is also installed when
    /// missing (the approve/reject handlers depend on it for the decision-confirmation
    /// card). Hosts that wire a custom renderer can register it BEFORE calling this helper
    /// — <c>TryAddSingleton</c> leaves explicit registrations in place.
    /// </para>
    /// <para>
    /// Handlers are registered as <c>AddSingleton&lt;ICommandHandler, T&gt;</c> (not
    /// <c>TryAddSingleton</c>) on purpose: the dispatcher resolves the full
    /// <see cref="IEnumerable{T}"/> of handlers from DI and the per-implementation
    /// uniqueness invariant is enforced by <see cref="CommandDispatcher"/>'s constructor.
    /// To prevent the same handler implementation type being registered twice when this
    /// helper is invoked more than once, each <see cref="ICommandHandler"/> registration
    /// is guarded with <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddTeamsCommandDispatcher(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddInProcessInboundEventChannel();
        services.TryAddSingleton<IAdaptiveCardRenderer, AdaptiveCardBuilder>();

        // Default IAgentSwarmStatusProvider wiring — the no-op default returns an empty
        // status list so StatusCommandHandler is wirable in DI without a real orchestrator
        // attached. Hosts override by registering a concrete provider BEFORE calling this
        // helper; TryAddSingleton leaves explicit registrations untouched.
        services.TryAddSingleton<IAgentSwarmStatusProvider, NullAgentSwarmStatusProvider>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandHandler, AskCommandHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandHandler, StatusCommandHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandHandler, ApproveCommandHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandHandler, RejectCommandHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandHandler, EscalateCommandHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandHandler, PauseCommandHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandHandler, ResumeCommandHandler>());

        services.TryAddSingleton<ICommandDispatcher, CommandDispatcher>();

        return services;
    }

    /// <summary>
    /// Composes the Stage 3.3 card-lifecycle dependency graph in a single call: registers
    /// <see cref="TeamsMessengerConnector"/> under <see cref="ITeamsCardManager"/>
    /// (delegating to the existing singleton wired by
    /// <see cref="AddTeamsMessengerConnector"/>), the concrete
    /// <see cref="Cards.CardActionHandler"/> under
    /// <see cref="ICardActionHandler"/> (<b>replacing</b> the Stage 2.1 <c>NoOpCardActionHandler</c>),
    /// and the <see cref="QuestionExpiryProcessor"/> hosted service so the lifecycle worker
    /// boots with the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Persistence-store registration is intentionally NOT included</b>: the SQL-backed
    /// <c>IAgentQuestionStore</c> and <c>ICardStateStore</c> implementations live in the
    /// <c>AgentSwarm.Messaging.Teams.EntityFrameworkCore</c> package and are wired by
    /// <c>AddSqlAgentQuestionStore()</c> / <c>AddSqlCardStateStore()</c> on the EF
    /// service-collection extensions. Hosts compose those helpers BEFORE calling this
    /// method so the lifecycle worker sees the production stores rather than the
    /// Stage 2.1 in-memory stubs.
    /// </para>
    /// <para>
    /// <b>Iter-8 fix:</b> the <see cref="ICardActionHandler"/> and
    /// <see cref="ITeamsCardManager"/> registrations now use
    /// <see cref="ServiceCollectionDescriptorExtensions.RemoveAll{T}(IServiceCollection)"/>
    /// + <see cref="ServiceCollectionServiceExtensions.AddSingleton{TService}(IServiceCollection, Func{IServiceProvider, TService})"/>
    /// rather than <c>TryAddSingleton</c>. This unconditionally replaces any Stage 2.1
    /// no-op stub previously registered for either contract — the implementation-plan
    /// requirement that the concrete Stage 3.3 handler/connector wins regardless of
    /// composition order.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddTeamsCardLifecycle(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        // Compose the base connector first so TryAddSingleton<TeamsMessengerConnector>()
        // there resolves the same instance we register under ITeamsCardManager below.
        services.AddTeamsMessengerConnector();

        // Connector is the canonical ITeamsCardManager implementation per
        // architecture.md §4.1.1 ("TeamsMessengerConnector implements both
        // IMessengerConnector and ITeamsCardManager"). Replace any prior stub
        // registration so hosts that wired a no-op in Stage 2.1 get the concrete here.
        services.RemoveAll<ITeamsCardManager>();
        services.AddSingleton<ITeamsCardManager>(
            sp => sp.GetRequiredService<TeamsMessengerConnector>());

        // Replace the Stage 2.1 NoOpCardActionHandler (or any other prior stub) with the
        // concrete CardActionHandler implementation.
        services.RemoveAll<ICardActionHandler>();
        services.AddSingleton<ICardActionHandler, CardActionHandler>();

        // Lifecycle worker — singleton per BackgroundService convention. Registered via
        // AddHostedService<T>() so the runtime picks it up automatically.
        services.AddHostedService<QuestionExpiryProcessor>();

        return services;
    }

    /// <summary>
    /// Register the Stage 4.2 <see cref="TeamsProactiveNotifier"/> as a singleton under
    /// both the concrete type and <see cref="IProactiveNotifier"/>. Intended for hosts
    /// that need to drive agent-originated proactive deliveries (the Phase 6 outbox
    /// engine, the orchestrator's "ask a question" trigger, etc.) without going through
    /// an inbound activity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The notifier shares its <c>CloudAdapter</c>,
    /// <see cref="IConversationReferenceStore"/>, <see cref="ICardStateStore"/>,
    /// <see cref="IAgentQuestionStore"/>, and <see cref="Cards.IAdaptiveCardRenderer"/>
    /// dependencies with <see cref="TeamsMessengerConnector"/>; this helper therefore
    /// composes <see cref="AddTeamsMessengerConnector"/> first so the shared
    /// <see cref="Cards.IAdaptiveCardRenderer"/> default registration (and the
    /// <see cref="IConversationReferenceRouter"/> cast-adapter) are in place.
    /// </para>
    /// <para>
    /// <b>Host-supplied dependencies</b>: <see cref="AddTeamsMessengerConnector"/> does
    /// <i>not</i> register the underlying <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/>,
    /// <see cref="IConversationReferenceStore"/>, <see cref="IAgentQuestionStore"/>,
    /// <see cref="ICardStateStore"/>, <see cref="TeamsMessagingOptions"/>, or the
    /// <see cref="Microsoft.Extensions.Logging.ILogger{T}"/> the notifier consumes —
    /// those are owned by the host application (typically: the EF Core extension
    /// package's <c>AddSql*Store</c> helpers and the host's
    /// <see cref="Microsoft.Extensions.Logging.LoggerFactory"/> wiring). The host MUST
    /// register all of them BEFORE calling this helper, otherwise resolving
    /// <see cref="TeamsProactiveNotifier"/> from the built provider will throw the
    /// canonical <see cref="InvalidOperationException"/> for a missing service. The only
    /// dependencies <i>auto-wired</i> by <see cref="AddTeamsMessengerConnector"/> (and
    /// therefore satisfied transitively by this helper) are the default
    /// <see cref="Cards.IAdaptiveCardRenderer"/> (
    /// <see cref="Cards.AdaptiveCardBuilder"/>) and the
    /// <see cref="IConversationReferenceRouter"/> cast-adapter that re-exposes the
    /// host-supplied store under the router contract.
    /// </para>
    /// <para>
    /// <b>Idempotent</b> — every registration uses <c>TryAdd*</c> variants so calling
    /// this helper multiple times leaves the descriptor count unchanged, and explicit
    /// pre-registrations of either service type are preserved.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddTeamsProactiveNotifier(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        // Compose the shared default graph: the IAdaptiveCardRenderer default and the
        // IConversationReferenceRouter cast-adapter are populated by
        // AddTeamsMessengerConnector. The host is responsible for registering
        // CloudAdapter, IConversationReferenceStore, IAgentQuestionStore,
        // ICardStateStore, TeamsMessagingOptions, and ILogger<T> before resolving the
        // notifier from the built provider — see the remarks on this method for the
        // full list and the rationale.
        services.AddTeamsMessengerConnector();

        // Stage 5.1 iter-4 evaluator feedback item 2 — pin the notifier registration to a
        // factory that explicitly resolves the canonical 9-arg constructor (which carries
        // the InstallationStateGate). Mirrors the connector wiring above. Without this,
        // the .NET DI activator picks a shorter overload that delegates with
        // installationStateGate: null and the install-state pre-check never runs for
        // outbox-driven sends.
        services.TryAddSingleton<TeamsProactiveNotifier>(sp => new TeamsProactiveNotifier(
            adapter: sp.GetRequiredService<Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter>(),
            options: sp.GetRequiredService<TeamsMessagingOptions>(),
            conversationReferenceStore: sp.GetRequiredService<IConversationReferenceStore>(),
            cardRenderer: sp.GetRequiredService<IAdaptiveCardRenderer>(),
            cardStateStore: sp.GetRequiredService<ICardStateStore>(),
            agentQuestionStore: sp.GetRequiredService<IAgentQuestionStore>(),
            logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TeamsProactiveNotifier>>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            installationStateGate: sp.GetRequiredService<InstallationStateGate>()));
        services.TryAddSingleton<IProactiveNotifier>(sp => sp.GetRequiredService<TeamsProactiveNotifier>());

        return services;
    }

    /// <summary>
    /// Keyed-service key under which the un-decorated <see cref="TeamsProactiveNotifier"/>
    /// is registered as <see cref="IProactiveNotifier"/> for resolution by
    /// <see cref="Outbox.TeamsOutboxDispatcher"/>. Public so tests can resolve the inner
    /// notifier without picking up the outbox decorator.
    /// </summary>
    public const string InnerProactiveNotifierKey = "teams.inner";

    /// <summary>
    /// Keyed-service key under which the un-decorated <see cref="TeamsMessengerConnector"/>
    /// is registered as <see cref="IMessengerConnector"/> for resolution by
    /// <see cref="Outbox.TeamsOutboxDispatcher"/>. Public so tests can resolve the inner
    /// connector without picking up the outbox decorator.
    /// </summary>
    public const string InnerMessengerConnectorKey = "teams.inner";
}
