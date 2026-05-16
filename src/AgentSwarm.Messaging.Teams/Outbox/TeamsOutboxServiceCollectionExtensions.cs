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
    /// <c>AddSqlMessageOutbox</c>) BEFORE calling this helper — the decorator wiring
    /// captures the prior registration via <see cref="ServiceCollectionDescriptorExtensions.RemoveAll{T}(IServiceCollection)"/>
    /// and re-introduces it under a marker interface (<see cref="IInnerTeamsProactiveNotifier"/>
    /// / <see cref="IInnerTeamsMessengerConnector"/>) so the decorator can forward to it
    /// while the public interface points to the outbox-backed wrapper.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> Calling this method more than once is a no-op for the inner
    /// marker registrations — the second call finds the marker already present and skips
    /// the swap. The hosted service registration is guarded by
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
    /// so the engine boots exactly once.
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

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IMessengerConnector))
            ?? throw new InvalidOperationException(
                "AddTeamsOutboxEngine requires IMessengerConnector to be registered before it is called. " +
                "Call services.AddTeamsMessengerConnector() first.");

        services.Remove(inner);

        if (inner.ImplementationInstance is not null)
        {
            services.AddSingleton(typeof(IInnerTeamsMessengerConnector),
                new InnerMessengerConnectorAdapter((IMessengerConnector)inner.ImplementationInstance));
        }
        else if (inner.ImplementationFactory is not null)
        {
            var factory = inner.ImplementationFactory;
            services.AddSingleton<IInnerTeamsMessengerConnector>(
                sp => new InnerMessengerConnectorAdapter((IMessengerConnector)factory(sp)));
        }
        else if (inner.ImplementationType is not null)
        {
            var implType = inner.ImplementationType;
            services.AddSingleton<IInnerTeamsMessengerConnector>(sp =>
            {
                var resolved = (IMessengerConnector)ActivatorUtilities.GetServiceOrCreateInstance(sp, implType);
                return new InnerMessengerConnectorAdapter(resolved);
            });
        }
        else
        {
            throw new InvalidOperationException(
                "AddTeamsOutboxEngine could not adapt the existing IMessengerConnector registration: " +
                "the ServiceDescriptor exposed neither an instance, factory, nor implementation type.");
        }

        services.AddSingleton<IMessengerConnector>(sp => new OutboxBackedMessengerConnector(
            sp.GetRequiredService<IInnerTeamsMessengerConnector>().Inner,
            sp.GetRequiredService<IMessageOutbox>(),
            sp.GetRequiredService<IConversationReferenceRouter>(),
            sp.GetRequiredService<ILogger<OutboxBackedMessengerConnector>>(),
            sp.GetService<TimeProvider>()));
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
