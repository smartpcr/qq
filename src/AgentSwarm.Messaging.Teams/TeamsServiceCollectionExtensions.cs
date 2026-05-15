using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
/// Also fills in a default <see cref="IConversationReferenceRouter"/> registration that
/// adapts the registered <see cref="IConversationReferenceStore"/> singleton — the
/// canonical store implementations (Stage 2.1 in-memory + Stage 4.1 SQL) implement BOTH
/// interfaces, so a single store registration satisfies the connector's eight ctor
/// dependencies in production DI without forcing every host to write a router
/// registration of its own. Hosts that ship a router-only implementation can still
/// register it BEFORE calling this helper; the <c>TryAdd*</c> idempotency guard leaves
/// the explicit registration untouched.
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
        services.TryAddSingleton<TeamsMessengerConnector>();
        services.TryAddKeyedSingleton<IMessengerConnector>(
            MessengerKey,
            (sp, _) => sp.GetRequiredService<TeamsMessengerConnector>());

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
}
