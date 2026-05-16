using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// DI registration helpers for the Stage 6.1 outbox engine and its decorators. Builds on
/// the lower-level <c>AddTeamsProactiveNotifier</c> / <c>AddTeamsMessengerConnector</c>
/// helpers in <see cref="TeamsServiceCollectionExtensions"/> and the
/// <c>AddSqlMessageOutbox</c> helper in the EF Core extension package.
/// </summary>
public static class TeamsOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Compose the Stage 6.1 outbox graph end-to-end: configure <see cref="OutboxOptions"/>,
    /// register <see cref="OutboxMetrics"/> and <see cref="TokenBucketRateLimiter"/>,
    /// register <see cref="TeamsOutboxDispatcher"/> under <see cref="IOutboxDispatcher"/>,
    /// wrap the host-supplied <see cref="IProactiveNotifier"/> and
    /// <see cref="IMessengerConnector"/> singletons with the
    /// <see cref="OutboxBackedProactiveNotifier"/> /
    /// <see cref="OutboxBackedMessengerConnector"/> decorators, and start the
    /// <see cref="OutboxRetryEngine"/> as a hosted service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Composition order requirement.</b> The host MUST register the inner notifier
    /// and connector (via <c>AddTeamsProactiveNotifier</c> +
    /// <c>AddTeamsMessengerConnector</c> + <c>AddSqlMessageOutbox</c>) BEFORE calling
    /// this helper — the decorator wiring captures the prior registration and
    /// re-introduces it under a marker interface
    /// (<see cref="IInnerTeamsProactiveNotifier"/> /
    /// <see cref="IInnerTeamsMessengerConnector"/>) so the decorator can forward to it
    /// while the public interface points to the outbox-backed wrapper.
    /// </para>
    /// <para>
    /// <b>Connector source discovery (iter-2 fix).</b>
    /// <see cref="TeamsServiceCollectionExtensions.AddTeamsMessengerConnector"/> registers
    /// <see cref="IMessengerConnector"/> ONLY as a keyed service
    /// (<see cref="TeamsServiceCollectionExtensions.MessengerKey"/> = <c>"teams"</c>) and
    /// the concrete <see cref="TeamsMessengerConnector"/> singleton. To make the
    /// canonical composition (<c>AddTeamsMessengerConnector</c> +
    /// <c>AddTeamsProactiveNotifier</c> + <c>AddTeamsOutboxEngine</c>) work without
    /// requiring the host to also register an unkeyed alias, the decorator scans for the
    /// inner connector in this preference order: an explicit unkeyed
    /// <see cref="IMessengerConnector"/> descriptor → the concrete
    /// <see cref="TeamsMessengerConnector"/> singleton → the keyed
    /// <see cref="IMessengerConnector"/> registration under
    /// <see cref="TeamsServiceCollectionExtensions.MessengerKey"/>. Once an inner is
    /// captured, the decorator installs the outbox-backed wrapper under both the
    /// unkeyed <see cref="IMessengerConnector"/> contract AND the keyed
    /// <see cref="TeamsServiceCollectionExtensions.MessengerKey"/> alias (if the keyed
    /// registration was present originally), so callers using either
    /// <see cref="ServiceProviderServiceExtensions.GetRequiredService{T}(IServiceProvider)"/>
    /// or
    /// <see cref="Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions.GetRequiredKeyedService{T}(IServiceProvider, object?)"/>
    /// always go through the outbox.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> Calling this method more than once is a no-op for the inner
    /// marker registrations — the second call finds the marker already present and skips
    /// the swap.
    /// </para>
    /// </remarks>
    /// <param name="services">DI container.</param>
    /// <param name="configure">Optional configuration callback for <see cref="OutboxOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTeamsOutboxEngine(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // OutboxOptions singleton — replace any prior registration so the host's
        // configure callback is honoured (TryAddSingleton would silently keep an
        // AddSqlMessageOutbox-provided default with no overrides applied).
        var options = new OutboxOptions();
        configure?.Invoke(options);
        services.RemoveAll<OutboxOptions>();
        services.AddSingleton(options);

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<OutboxMetrics>();
        services.TryAddSingleton<TokenBucketRateLimiter>();

        services.RemoveAll<IOutboxDispatcher>();
        services.AddSingleton<IOutboxDispatcher, TeamsOutboxDispatcher>();

        // Decorate IProactiveNotifier. The host MUST have registered the inner
        // notifier first (typically via AddTeamsProactiveNotifier). Resolve the existing
        // descriptor, expose it under a private inner marker, and route the public
        // interface to the outbox-backed wrapper. The same applies to IMessengerConnector.
        DecorateProactiveNotifier(services);
        DecorateMessengerConnector(services);

        services.AddHostedService<OutboxRetryEngine>();

        return services;
    }

    private static void DecorateProactiveNotifier(IServiceCollection services)
    {
        // Already decorated — skip.
        if (services.Any(d => d.ServiceType == typeof(IInnerTeamsProactiveNotifier)))
        {
            return;
        }

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IProactiveNotifier))
            ?? throw new InvalidOperationException(
                "AddTeamsOutboxEngine requires IProactiveNotifier to be registered before it is called. " +
                "Call services.AddTeamsProactiveNotifier() (or your own IProactiveNotifier registration) first.");

        services.Remove(inner);

        // Re-expose the inner registration under the marker interface so the dispatcher
        // can resolve it without resolving the now-decorated public interface (which
        // would create an infinite loop).
        if (inner.ImplementationInstance is not null)
        {
            services.AddSingleton(typeof(IInnerTeamsProactiveNotifier),
                new InnerProactiveNotifierAdapter((IProactiveNotifier)inner.ImplementationInstance));
        }
        else if (inner.ImplementationFactory is not null)
        {
            var factory = inner.ImplementationFactory;
            services.AddSingleton<IInnerTeamsProactiveNotifier>(
                sp => new InnerProactiveNotifierAdapter((IProactiveNotifier)factory(sp)));
        }
        else if (inner.ImplementationType is not null)
        {
            var implType = inner.ImplementationType;
            services.AddSingleton<IInnerTeamsProactiveNotifier>(sp =>
            {
                var resolved = (IProactiveNotifier)ActivatorUtilities.GetServiceOrCreateInstance(sp, implType);
                return new InnerProactiveNotifierAdapter(resolved);
            });
        }
        else
        {
            throw new InvalidOperationException(
                "AddTeamsOutboxEngine could not adapt the existing IProactiveNotifier registration: " +
                "the ServiceDescriptor exposed neither an instance, factory, nor implementation type.");
        }

        // Public IProactiveNotifier is now the outbox-backed decorator.
        services.AddSingleton<IProactiveNotifier>(sp => new OutboxBackedProactiveNotifier(
            sp.GetRequiredService<IMessageOutbox>(),
            sp.GetRequiredService<IConversationReferenceStore>(),
            sp.GetRequiredService<ILogger<OutboxBackedProactiveNotifier>>(),
            sp.GetService<TimeProvider>()));
    }

    private static void DecorateMessengerConnector(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IInnerTeamsMessengerConnector)))
        {
            return;
        }

        // Source discovery — see the XML remarks on AddTeamsOutboxEngine for the preference
        // order rationale. The canonical Teams wiring registers the concrete
        // TeamsMessengerConnector singleton AND the keyed "teams" IMessengerConnector
        // (factory: sp => sp.GetRequiredService<TeamsMessengerConnector>()) but does NOT
        // register an unkeyed IMessengerConnector — so the decorator MUST treat the keyed
        // alias or the concrete singleton as a valid inner source, otherwise the canonical
        // composition (the one the docs/tests advertise) throws the "missing
        // IMessengerConnector" error.
        var unkeyed = services.LastOrDefault(IsUnkeyedMessengerConnector);
        var keyedTeams = services.LastOrDefault(IsKeyedTeamsMessengerConnector);
        var concreteTeams = services.LastOrDefault(d => d.ServiceType == typeof(TeamsMessengerConnector));

        if (unkeyed is null && keyedTeams is null && concreteTeams is null)
        {
            throw new InvalidOperationException(
                "AddTeamsOutboxEngine requires IMessengerConnector to be registered before it is called. " +
                "Call services.AddTeamsMessengerConnector() first (the canonical Teams wiring, which " +
                "registers both the concrete TeamsMessengerConnector singleton and the keyed " +
                $"IMessengerConnector(\"{TeamsServiceCollectionExtensions.MessengerKey}\") alias), or register an unkeyed " +
                "IMessengerConnector yourself before invoking AddTeamsOutboxEngine.");
        }

        if (unkeyed is not null)
        {
            // Legacy / manual wiring path — host added an explicit unkeyed registration.
            // Capture the descriptor's factory/instance/type so the inner adapter can
            // forward to it without going through the now-decorated unkeyed contract.
            services.Remove(unkeyed);
            RegisterInnerMessengerAdapterFromDescriptor(services, unkeyed);
        }
        else if (concreteTeams is not null)
        {
            // Canonical Teams wiring path — resolve via the concrete TeamsMessengerConnector
            // singleton. This is cycle-safe because the concrete service type is not
            // decorated by this method (only the IMessengerConnector contracts are).
            services.AddSingleton<IInnerTeamsMessengerConnector>(sp =>
                new InnerMessengerConnectorAdapter(sp.GetRequiredService<TeamsMessengerConnector>()));
        }
        else
        {
            // Keyed-only path — no concrete singleton exposed (host registered the keyed
            // alias by hand with a custom factory). Snapshot the keyed factory/instance
            // into the inner adapter and remove the original keyed descriptor so the
            // upcoming keyed rebind below does not create a self-referential cycle.
            var keyDescriptor = keyedTeams!;
            services.Remove(keyDescriptor);
            services.AddSingleton<IInnerTeamsMessengerConnector>(
                BuildInnerFromKeyedDescriptor(keyDescriptor));
        }

        // Always install the unkeyed IMessengerConnector wrapper — this is what
        // GetRequiredService<IMessengerConnector>() resolves to.
        services.AddSingleton<IMessengerConnector>(sp => new OutboxBackedMessengerConnector(
            sp.GetRequiredService<IInnerTeamsMessengerConnector>().Inner,
            sp.GetRequiredService<IMessageOutbox>(),
            sp.GetRequiredService<IConversationReferenceRouter>(),
            sp.GetRequiredService<ILogger<OutboxBackedMessengerConnector>>(),
            sp.GetService<TimeProvider>()));

        // Rebind the keyed "teams" alias (if it existed in the original graph) to the
        // wrapper so GetRequiredKeyedService<IMessengerConnector>("teams") also goes
        // through the outbox. Without this step the keyed alias still resolves to the
        // pre-decoration TeamsMessengerConnector and the canonical Teams composition
        // silently bypasses the outbox for keyed-resolution callers — the iter-1
        // evaluator critique.
        var hadKeyedAlias = keyedTeams is not null || concreteTeams is not null;
        var staleKeyed = services.Where(IsKeyedTeamsMessengerConnector).ToList();
        foreach (var d in staleKeyed)
        {
            services.Remove(d);
        }
        if (hadKeyedAlias)
        {
            services.AddKeyedSingleton<IMessengerConnector>(
                TeamsServiceCollectionExtensions.MessengerKey,
                (sp, _) => sp.GetRequiredService<IMessengerConnector>());
        }
    }

    private static bool IsUnkeyedMessengerConnector(ServiceDescriptor d) =>
        d.ServiceType == typeof(IMessengerConnector) && !d.IsKeyedService;

    private static bool IsKeyedTeamsMessengerConnector(ServiceDescriptor d) =>
        d.ServiceType == typeof(IMessengerConnector)
        && d.IsKeyedService
        && Equals(d.ServiceKey, TeamsServiceCollectionExtensions.MessengerKey);

    private static void RegisterInnerMessengerAdapterFromDescriptor(
        IServiceCollection services, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is not null)
        {
            services.AddSingleton(typeof(IInnerTeamsMessengerConnector),
                new InnerMessengerConnectorAdapter((IMessengerConnector)descriptor.ImplementationInstance));
            return;
        }
        if (descriptor.ImplementationFactory is not null)
        {
            var factory = descriptor.ImplementationFactory;
            services.AddSingleton<IInnerTeamsMessengerConnector>(
                sp => new InnerMessengerConnectorAdapter((IMessengerConnector)factory(sp)));
            return;
        }
        if (descriptor.ImplementationType is not null)
        {
            var implType = descriptor.ImplementationType;
            services.AddSingleton<IInnerTeamsMessengerConnector>(sp =>
                new InnerMessengerConnectorAdapter(
                    (IMessengerConnector)ActivatorUtilities.GetServiceOrCreateInstance(sp, implType)));
            return;
        }
        throw new InvalidOperationException(
            "AddTeamsOutboxEngine could not adapt the existing IMessengerConnector registration: " +
            "the ServiceDescriptor exposed neither an instance, factory, nor implementation type.");
    }

    private static Func<IServiceProvider, IInnerTeamsMessengerConnector> BuildInnerFromKeyedDescriptor(
        ServiceDescriptor descriptor)
    {
        if (descriptor.KeyedImplementationInstance is not null)
        {
            var instance = (IMessengerConnector)descriptor.KeyedImplementationInstance;
            return _ => new InnerMessengerConnectorAdapter(instance);
        }
        if (descriptor.KeyedImplementationFactory is not null)
        {
            var factory = descriptor.KeyedImplementationFactory;
            var key = descriptor.ServiceKey;
            return sp => new InnerMessengerConnectorAdapter((IMessengerConnector)factory(sp, key));
        }
        if (descriptor.KeyedImplementationType is not null)
        {
            var implType = descriptor.KeyedImplementationType;
            return sp => new InnerMessengerConnectorAdapter(
                (IMessengerConnector)ActivatorUtilities.GetServiceOrCreateInstance(sp, implType));
        }
        throw new InvalidOperationException(
            "AddTeamsOutboxEngine could not adapt the keyed IMessengerConnector registration: " +
            "the ServiceDescriptor exposed neither a keyed instance, factory, nor implementation type.");
    }
}

/// <summary>
/// Internal marker used by <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/>
/// to expose the pre-decoration <see cref="IProactiveNotifier"/> registration to the
/// outbox dispatcher without re-introducing a circular DI graph. Concrete tests can wire
/// a custom adapter under this marker for isolation.
/// </summary>
public interface IInnerTeamsProactiveNotifier
{
    /// <summary>The wrapped notifier.</summary>
    IProactiveNotifier Inner { get; }
}

/// <summary>Default <see cref="IInnerTeamsProactiveNotifier"/> implementation.</summary>
internal sealed class InnerProactiveNotifierAdapter : IInnerTeamsProactiveNotifier
{
    public InnerProactiveNotifierAdapter(IProactiveNotifier inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public IProactiveNotifier Inner { get; }
}

/// <summary>
/// Internal marker used by <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/>
/// to expose the pre-decoration <see cref="IMessengerConnector"/> registration.
/// </summary>
public interface IInnerTeamsMessengerConnector
{
    /// <summary>The wrapped connector.</summary>
    IMessengerConnector Inner { get; }
}

/// <summary>Default <see cref="IInnerTeamsMessengerConnector"/> implementation.</summary>
internal sealed class InnerMessengerConnectorAdapter : IInnerTeamsMessengerConnector
{
    public InnerMessengerConnectorAdapter(IMessengerConnector inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public IMessengerConnector Inner { get; }
}
