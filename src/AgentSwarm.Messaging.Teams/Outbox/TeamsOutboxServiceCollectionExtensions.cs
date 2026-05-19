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
    /// this helper — the decorator wiring captures the prior registrations and
    /// re-introduces them under marker interfaces
    /// (<see cref="IInnerTeamsProactiveNotifier"/> /
    /// <see cref="IInnerTeamsMessengerConnector"/>). Only the
    /// <see cref="IInnerTeamsMessengerConnector"/> marker is consumed at runtime, and
    /// only for the single <see cref="IMessengerConnector.ReceiveAsync"/> forwarding
    /// call (inbound events do not flow through the outbox by design).
    /// <see cref="OutboxBackedProactiveNotifier"/> takes NO inner dependency at all —
    /// every send method enqueues directly to <see cref="IMessageOutbox"/>, and the
    /// <see cref="IInnerTeamsProactiveNotifier"/> registration is reserved for
    /// test / diagnostic resolution of the pre-decoration notifier (e.g. to pin
    /// underlying send semantics in unit tests that do not start the outbox engine).
    /// <see cref="TeamsOutboxDispatcher"/> consumes neither marker — it owns
    /// <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/>
    /// directly and dispatches each dequeued entry via
    /// <c>CloudAdapter.ContinueConversationAsync</c>.
    /// </para>
    /// <para>
    /// <b>Direct-send bypass boundary.</b>
    /// Even though this helper re-exposes the inner concretes under markers, the
    /// concrete <see cref="TeamsMessengerConnector"/> and
    /// <see cref="TeamsProactiveNotifier"/> classes still own direct
    /// <c>CloudAdapter.ContinueConversationAsync</c> calls in their send methods.
    /// Without a guard those direct paths would silently bypass
    /// <see cref="IMessageOutbox.EnqueueAsync"/> for any production caller that
    /// resolved the concrete type or the marker's
    /// <see cref="IInnerTeamsMessengerConnector.Inner"/> /
    /// <see cref="IInnerTeamsProactiveNotifier.Inner"/> accessor and called send on
    /// it. To close that bypass, this helper registers
    /// <see cref="TeamsDirectSendBypassGuard"/> as a sentinel singleton; the
    /// connector / notifier factories in
    /// <see cref="TeamsServiceCollectionExtensions"/> wire the guard onto the
    /// concretes via <see cref="TeamsMessengerConnector.DirectSendGuard"/> /
    /// <see cref="TeamsProactiveNotifier.DirectSendGuard"/> property initializers,
    /// and every concrete send method calls
    /// <see cref="TeamsDirectSendBypassGuard.ThrowIfDisallowed"/> at the top — which
    /// converts the silent bypass into a loud <see cref="InvalidOperationException"/>
    /// with a remediation message pointing the caller back at the public
    /// <see cref="IMessengerConnector"/> / <see cref="IProactiveNotifier"/>
    /// contracts. Tests and legacy compositions that do not call
    /// <c>AddTeamsOutboxEngine</c> never see the guard registered, so their existing
    /// direct-send semantics are preserved unchanged.
    /// </para>
    /// <para>
    /// <b>Connector source discovery.</b>
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
    /// captured, the decorator installs the outbox-backed wrapper under the unkeyed
    /// <see cref="IMessengerConnector"/> contract. The keyed alias under
    /// <see cref="TeamsServiceCollectionExtensions.MessengerKey"/> is rebound to the
    /// wrapper <b>only when the host originally registered a keyed descriptor</b> — when
    /// the host registered only the concrete <see cref="TeamsMessengerConnector"/>
    /// singleton (without ever asking for the keyed alias), no synthetic keyed alias is
    /// introduced. This preserves the host's wiring shape exactly.
    /// </para>
    /// <para>
    /// <b>Cycle safety.</b> Two cases require care when the host registered both an
    /// unkeyed <see cref="IMessengerConnector"/> FACTORY and a keyed
    /// <see cref="IMessengerConnector"/> under
    /// <see cref="TeamsServiceCollectionExtensions.MessengerKey"/>:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Alias factory</b> — e.g.
    ///     <c>sp =&gt; sp.GetRequiredKeyedService&lt;IMessengerConnector&gt;(MessengerKey)</c>.
    ///     After the keyed rebind below points the keyed alias at the wrapper, a naive
    ///     inner adapter executing this factory would resolve back through the wrapper,
    ///     and the wrapper depends on the inner, producing a stack overflow at first
    ///     resolution.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Legitimate factory</b> — e.g. <c>sp =&gt; new CustomConnector(...)</c>
    ///     that does <i>not</i> touch the keyed alias. Discarding such a factory
    ///     whenever a keyed sibling is present would silently drop the host's explicit
    ///     choice.
    ///   </description></item>
    /// </list>
    /// To preserve BOTH host shapes, the decorator captures the unkeyed factory and
    /// invokes it lazily through a
    /// <c>KeyedTeamsConnectorInterceptor</c> wrapper around the production
    /// <see cref="IServiceProvider"/>. The interceptor implements
    /// <see cref="IKeyedServiceProvider"/> and intercepts ONLY the
    /// <c>(typeof(IMessengerConnector), MessengerKey)</c> pair: that single lookup is
    /// served from a pre-rebind snapshot of the keyed descriptor (so alias factories
    /// return the original concrete connector). All other lookups — including
    /// <c>GetRequiredService&lt;CustomDep&gt;()</c> calls the legitimate factory might
    /// make — pass through unchanged. The result: alias factories no longer cycle, and
    /// legitimate factories continue to take precedence over the keyed registration.
    /// Unkeyed descriptors backed by an
    /// <see cref="ServiceDescriptor.ImplementationInstance"/> or
    /// <see cref="ServiceDescriptor.ImplementationType"/> are not at risk and continue
    /// to take precedence via the same path (the interceptor only matters when the
    /// host's wiring is itself a factory).
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

        // Fail fast — symmetric with the IProactiveNotifier / IMessengerConnector
        // checks inside DecorateProactiveNotifier / DecorateMessengerConnector. The
        // dispatcher and the outbox-backed decorators resolve IMessageOutbox via
        // GetRequiredService at request time; without this guard the host gets a
        // hostile InvalidOperationException at the FIRST send attempt with no hint
        // that the real fix is "call AddSqlMessageOutbox before AddTeamsOutboxEngine".
        // We deliberately run this check BEFORE any service-collection mutation so a
        // misconfigured container is left in its original state.
        if (!services.Any(d => d.ServiceType == typeof(IMessageOutbox)))
        {
            throw new InvalidOperationException(
                "AddTeamsOutboxEngine requires an IMessageOutbox registration. Call " +
                "services.AddSqlMessageOutbox(...) (the canonical Teams EF Core wiring) " +
                "or register your own IMessageOutbox implementation BEFORE invoking " +
                "AddTeamsOutboxEngine. Without IMessageOutbox the outbox-backed " +
                "decorators have nothing to enqueue against.");
        }

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

        // Stage 6.2 step 4 — outbound deduplication singleton + background eviction
        // service. Registered via TryAdd* so hosts that supplied a custom
        // OutboundDeduplicationOptions or replaced the deduplicator with a no-op for
        // tests keep their override. The eviction service purges entries older than
        // OutboundDeduplicationOptions.Window on the configured cadence.
        services.TryAddSingleton<OutboundDeduplicationOptions>();
        services.TryAddSingleton<OutboundMessageDeduplicator>(sp => new OutboundMessageDeduplicator(
            sp.GetRequiredService<OutboundDeduplicationOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<OutboundDeduplicationEvictionService>();

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
            sp.GetRequiredService<IAgentQuestionStore>(),
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
            // Use an IServiceProvider interceptor to handle the case where the
            // unkeyed registration is itself a factory and a keyed sibling exists.
            // The interceptor intercepts ONLY the (IMessengerConnector, MessengerKey)
            // pair and routes it to a pre-rebind snapshot of the keyed source. Alias
            // factories (sp => sp.GetRequiredKeyedService("teams")) get the original
            // concrete instance and never loop through the wrapper; legitimate
            // factories (sp => new CustomConnector(...)) execute normally and their
            // result becomes the captured inner, preserving the host's explicit
            // choice over the keyed registration.
            if (unkeyed.ImplementationFactory is not null && keyedTeams is not null)
            {
                var capturedUnkeyedFactory = unkeyed.ImplementationFactory;
                var capturedKeyedSource = MaterialiseKeyedTeamsConnector(keyedTeams);
                services.Remove(unkeyed);
                services.AddSingleton<IInnerTeamsMessengerConnector>(sp =>
                {
                    var interceptor = new KeyedTeamsConnectorInterceptor(sp, capturedKeyedSource);
                    var inner = (IMessengerConnector)capturedUnkeyedFactory(interceptor);
                    return new InnerMessengerConnectorAdapter(inner);
                });
            }
            else
            {
                // Unkeyed is an Instance / Type / standalone-Factory (no keyed sibling to
                // alias into). Capture the descriptor directly — no interceptor required.
                services.Remove(unkeyed);
                RegisterInnerMessengerAdapterFromDescriptor(services, unkeyed);
            }
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
        // GetRequiredService<IMessengerConnector>() resolves to. Stage 6.2 step 4: the
        // wrapper receives the OutboundMessageDeduplicator so duplicate (CorrelationId,
        // ConversationId) sends are suppressed before an OutboxEntry is enqueued.
        services.AddSingleton<IMessengerConnector>(sp => new OutboxBackedMessengerConnector(
            sp.GetRequiredService<IInnerTeamsMessengerConnector>().Inner,
            sp.GetRequiredService<IMessageOutbox>(),
            sp.GetRequiredService<IConversationReferenceRouter>(),
            sp.GetRequiredService<IConversationReferenceStore>(),
            sp.GetRequiredService<IAgentQuestionStore>(),
            sp.GetRequiredService<ILogger<OutboxBackedMessengerConnector>>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<OutboundMessageDeduplicator>()));

        // Rebind the keyed "teams" alias to the wrapper ONLY when the host originally
        // registered a keyed alias (critique #3). The concrete TeamsMessengerConnector
        // singleton on its own does NOT imply the host opted into the keyed contract,
        // so we must not synthesise one — that would silently change the host's wiring
        // shape and violate the documented contract on AddTeamsOutboxEngine.
        var hadKeyedAlias = keyedTeams is not null;
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

    /// <summary>
    /// Materialise the original keyed Teams connector source into a closure that
    /// invokes the descriptor's instance / factory / type WITHOUT going through the
    /// keyed-lookup chain on the production <see cref="IServiceProvider"/>. Used by
    /// <see cref="KeyedTeamsConnectorInterceptor"/> so an unkeyed alias-factory that
    /// resolves the keyed connector receives the pre-rebind concrete instead of the
    /// outbox-backed wrapper (which would create a stack-overflow cycle).
    /// </summary>
    private static Func<IServiceProvider, IMessengerConnector> MaterialiseKeyedTeamsConnector(
        ServiceDescriptor descriptor)
    {
        if (descriptor.KeyedImplementationInstance is not null)
        {
            var instance = (IMessengerConnector)descriptor.KeyedImplementationInstance;
            return _ => instance;
        }
        if (descriptor.KeyedImplementationFactory is not null)
        {
            var factory = descriptor.KeyedImplementationFactory;
            var key = descriptor.ServiceKey;
            return sp => (IMessengerConnector)factory(sp, key);
        }
        if (descriptor.KeyedImplementationType is not null)
        {
            var implType = descriptor.KeyedImplementationType;
            return sp => (IMessengerConnector)ActivatorUtilities.GetServiceOrCreateInstance(sp, implType);
        }
        throw new InvalidOperationException(
            "AddTeamsOutboxEngine could not snapshot the keyed IMessengerConnector source: " +
            "the ServiceDescriptor exposed neither a keyed instance, factory, nor implementation type.");
    }

    /// <summary>
    /// <see cref="IServiceProvider"/> + <see cref="IKeyedServiceProvider"/> wrapper used
    /// during inner-connector capture. Forwards every lookup to the production provider
    /// EXCEPT the single <c>(IMessengerConnector, MessengerKey)</c> pair, which is
    /// served from a pre-rebind snapshot of the host's keyed descriptor. This breaks
    /// the alias-factory cycle without dropping legitimate host-supplied unkeyed
    /// factories.
    /// </summary>
    private sealed class KeyedTeamsConnectorInterceptor : IServiceProvider, IKeyedServiceProvider
    {
        private readonly IServiceProvider _inner;

        // Single-invocation guarantee for the captured keyed Teams connector. The
        // earlier shape was `_capturedKeyedTeamsResult ??= _capturedKeyedTeamsFactory(_inner)`,
        // which compiles to a non-atomic read-then-write pair on a reference field —
        // two concurrent callers that both observe the field as null would each invoke
        // the factory, materialising two connector instances. The loser of the write
        // race is silently overwritten and leaked along with whatever sockets,
        // background pumps, or auth tokens it captured. The container's own singleton
        // lock currently serialises construction here, so the race window is empty in
        // practice, but that's an implicit and fragile invariant: any future refactor
        // that resolves the interceptor outside that envelope would re-introduce the
        // bug with no obvious warning. Using Lazy<T> with the default constructor
        // (LazyThreadSafetyMode.ExecutionAndPublication) makes the at-most-once factory
        // invocation a type-enforced contract that survives such refactors.
        private readonly Lazy<IMessengerConnector> _capturedKeyedTeams;

        public KeyedTeamsConnectorInterceptor(
            IServiceProvider inner,
            Func<IServiceProvider, IMessengerConnector> capturedKeyedTeamsFactory)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            ArgumentNullException.ThrowIfNull(capturedKeyedTeamsFactory);
            _capturedKeyedTeams = new Lazy<IMessengerConnector>(
                () => capturedKeyedTeamsFactory(inner));
        }

        public object? GetService(Type serviceType) => _inner.GetService(serviceType);

        public object? GetKeyedService(Type serviceType, object? serviceKey)
        {
            if (serviceType == typeof(IMessengerConnector)
                && Equals(serviceKey, TeamsServiceCollectionExtensions.MessengerKey))
            {
                return _capturedKeyedTeams.Value;
            }
            return ((IKeyedServiceProvider)_inner).GetKeyedService(serviceType, serviceKey);
        }

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
        {
            if (serviceType == typeof(IMessengerConnector)
                && Equals(serviceKey, TeamsServiceCollectionExtensions.MessengerKey))
            {
                return _capturedKeyedTeams.Value;
            }
            return ((IKeyedServiceProvider)_inner).GetRequiredKeyedService(serviceType, serviceKey);
        }
    }
}

/// <summary>
/// Internal marker used by <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/>
/// to expose the pre-decoration <see cref="IProactiveNotifier"/> registration to
/// test/diagnostic code without re-introducing a circular DI graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>NOT consumed at runtime by production code.</b>
/// <see cref="OutboxBackedProactiveNotifier"/> takes no inner-notifier dependency, and
/// <see cref="TeamsOutboxDispatcher"/> owns
/// <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/> directly. This
/// marker is preserved so unit tests can pin the underlying
/// <see cref="TeamsProactiveNotifier"/> send semantics by resolving the pre-decoration
/// instance, and so audit/diagnostic tooling can reflect on the original registration.
/// Production hosts MUST resolve <see cref="IProactiveNotifier"/> directly — that points
/// to <see cref="OutboxBackedProactiveNotifier"/> which enqueues to
/// <see cref="IMessageOutbox"/>. Calling
/// <see cref="TeamsProactiveNotifier"/> send methods through this marker's
/// <see cref="Inner"/> accessor in production would normally constitute a direct-send
/// bypass; the <see cref="TeamsDirectSendBypassGuard"/> registered by
/// <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/> blocks
/// every such call with a structured
/// <see cref="System.InvalidOperationException"/>.
/// </para>
/// </remarks>
public interface IInnerTeamsProactiveNotifier
{
    /// <summary>The wrapped notifier (test/diagnostic access only — see remarks).</summary>
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
/// <remarks>
/// <para>
/// <b>Consumed for ReceiveAsync forwarding ONLY.</b>
/// <see cref="OutboxBackedMessengerConnector"/> resolves this marker so its
/// <see cref="IMessengerConnector.ReceiveAsync"/> implementation can delegate to the
/// inner concrete connector — inbound events do not flow through the outbox by design.
/// <see cref="TeamsOutboxDispatcher"/> does NOT resolve this marker; it owns
/// <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/> directly and
/// dispatches each dequeued <see cref="OutboxEntry"/> via
/// <c>CloudAdapter.ContinueConversationAsync</c>. Calling the inner connector's send
/// methods through this marker's <see cref="Inner"/> accessor in production would
/// constitute a direct-send bypass; the <see cref="TeamsDirectSendBypassGuard"/>
/// registered by
/// <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/> blocks
/// every such call with a structured
/// <see cref="System.InvalidOperationException"/>.
/// </para>
/// </remarks>
public interface IInnerTeamsMessengerConnector
{
    /// <summary>The wrapped connector (used for <see cref="IMessengerConnector.ReceiveAsync"/> forwarding — see remarks).</summary>
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
