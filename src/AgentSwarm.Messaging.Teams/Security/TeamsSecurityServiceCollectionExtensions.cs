using System.Linq;
using System.Net.Http;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// DI registration helpers for the Stage 5.1 security layer. Wires the concrete
/// implementations of <see cref="IIdentityResolver"/>, <see cref="IUserAuthorizationService"/>,
/// <see cref="TenantValidationMiddleware"/>, <see cref="InstallationStateGate"/>, and
/// <see cref="TeamsAppPolicyHealthCheck"/> in place of the default-deny stubs registered
/// by the Stage 2.1 host bootstrap.
/// </summary>
public static class TeamsSecurityServiceCollectionExtensions
{
    /// <summary>
    /// Register the Stage 5.1 Teams security graph. Idempotent — every registration uses
    /// <c>TryAdd*</c> variants and <c>RemoveAll&lt;T&gt;</c> for the two stub-replacing
    /// service types (<see cref="IIdentityResolver"/> and
    /// <see cref="IUserAuthorizationService"/>) so calling the helper twice produces the
    /// same descriptor set.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> instance (fluent).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddTeamsSecurity(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<IUserDirectory, StaticUserDirectory>();
        services.TryAddSingleton<IUserRoleProvider, StaticUserRoleProvider>();

        // RbacOptions / TeamsAppPolicyOptions are populated by the host's
        // services.Configure<TOptions>(configuration.GetSection(...)) call; this method
        // provides DEFAULT instances so unit tests and ad-hoc hosts can resolve the
        // services without first wiring IConfiguration. Hosts that bind from
        // configuration get the configured instance because OptionsManager merges
        // PostConfigure / Configure delegates over the initial instance.
        services.AddOptions<RbacOptions>().Configure(o => o.WithDefaultRoleMatrix());

        // Bridge the two TeamsMessagingOptions resolution surfaces so all host
        // registration styles produce one observable options instance.
        //
        // Why a bridge: the connector and proactive notifier resolve the concrete
        // TeamsMessagingOptions singleton (legacy direct-singleton pattern) while
        // TenantValidationMiddleware and the Entra BotFrameworkAuthentication factory
        // resolve IOptionsMonitor<TeamsMessagingOptions> (canonical IOptions pattern).
        // Without a bridge, a host wiring options via only one pattern leaves the
        // other consumer with empty defaults — tenant validation refuses every
        // request while the connector sends with the wrong AppId (or vice versa).
        //
        // Three forward-bridge variants (host pre-registered a concrete singleton)
        // plus one backward-bridge variant (host used services.Configure only) are
        // supported. Each forward variant projects from the SAME cached singleton
        // (instance / factory-resolved / type-resolved) into the IOptions surface so
        // both consumers observe identical values regardless of registration shape.
        // See the BridgeTeamsMessagingOptions remarks below for recursion-safety notes
        // when the host uses a factory or type-based registration.
        BridgeTeamsMessagingOptions(services);

        // TeamsAppPolicyOptions startup validation. The IValidateOptions implementation
        // runs Validate() on every resolution of IOptions<TeamsAppPolicyOptions>;
        // .ValidateOnStart() (composed with IHostedService) makes Host.StartAsync fail
        // fast when the bound options are invalid (e.g. unknown AllowedAppCatalogScopes
        // value) instead of deferring the surface to the health check.
        services.AddOptions<TeamsAppPolicyOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<TeamsAppPolicyOptions>, TeamsAppPolicyOptionsValidator>());

        // Replace the Stage 2.1 default-deny stubs with the concrete implementations.
        // RemoveAll (not TryAdd) is required because the stubs are already registered
        // by Stage 2.1 — TryAdd would silently leave them in place.
        services.RemoveAll<IIdentityResolver>();
        services.AddSingleton<IIdentityResolver, EntraIdentityResolver>();

        services.RemoveAll<IUserAuthorizationService>();
        services.AddSingleton<IUserAuthorizationService, RbacAuthorizationService>();

        services.TryAddSingleton<TenantValidationMiddleware>();
        services.TryAddSingleton<InstallationStateGate>();
        services.TryAddSingleton<TeamsAppPolicyHealthCheck>();

        // Auto-register the policy health check on the canonical name so the deployment
        // checklist's `GET /health → teams-app-policy: Healthy` instruction works without
        // a separate opt-in call. The registration is performed inside a deferred
        // PostConfigure<HealthCheckServiceOptions> delegate (not directly via
        // AddHealthChecks().AddCheck) so we can inspect the FINAL registration list at
        // service-provider build time. Two duplicate-prevention sources are handled:
        //   (a) repeated AddTeamsSecurity() calls — the TeamsAppPolicyHealthCheckMarker
        //       sentinel below short-circuits the second invocation;
        //   (b) a host that pre-registered a check with the same canonical name via
        //       services.AddHealthChecks().AddCheck("teams-app-policy", ...) before
        //       calling AddTeamsSecurity() — the PostConfigure delegate skips appending
        //       when a registration with the canonical name already exists, so the
        //       host's pre-existing check wins.
        if (!services.Any(d => d.ServiceType == typeof(TeamsAppPolicyHealthCheckMarker)))
        {
            services.AddSingleton<TeamsAppPolicyHealthCheckMarker>(_ => new TeamsAppPolicyHealthCheckMarker());

            // Ensure the HealthCheckService is registered; PostConfigure has no effect
            // if the host never composed AddHealthChecks(). Calling AddHealthChecks()
            // here is idempotent (it uses TryAddSingleton internally).
            services.AddHealthChecks();

            services.PostConfigure<HealthCheckServiceOptions>(o =>
            {
                if (o.Registrations.Any(r => string.Equals(r.Name, TeamsAppPolicyHealthCheck.Name, StringComparison.Ordinal)))
                {
                    return;
                }
                o.Registrations.Add(new HealthCheckRegistration(
                    name: TeamsAppPolicyHealthCheck.Name,
                    factory: sp => sp.GetRequiredService<TeamsAppPolicyHealthCheck>(),
                    failureStatus: HealthStatus.Degraded,
                    tags: new[] { "teams", "security" }));
            });
        }

        // BotFrameworkAuthentication hardening is part of the default security graph so
        // hosts composing AddTeamsSecurity() alone get JWT-layer AllowedCallers /
        // AllowedTenantIds enforcement instead of the SDK's unrestricted default
        // ConfigurationBotFrameworkAuthentication. Hosts that need to bind
        // EntraBotFrameworkAuthenticationOptions explicitly (e.g. from configuration)
        // can still call AddEntraBotFrameworkAuthentication(configure) directly — the
        // options builder is additive and the singleton registration RemoveAll+AddSingleton
        // pattern leaves exactly one descriptor regardless of call order.
        services.AddEntraBotFrameworkAuthentication();

        return services;
    }

    /// <summary>
    /// Bridge the concrete-singleton and IOptionsMonitor resolution surfaces for
    /// <see cref="TeamsMessagingOptions"/> so all host registration shapes produce
    /// identical observable values regardless of which surface consumers resolve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supports all three forward-bridge patterns when the host pre-registers a
    /// concrete singleton (instance, factory, or type), plus a backward bridge when
    /// the host called <c>services.Configure&lt;TeamsMessagingOptions&gt;</c> alone
    /// without a singleton. Idempotent across composed callers
    /// (<see cref="AddTeamsSecurity"/> and <c>AddTeamsMessengerConnector</c> both
    /// invoke it transitively): a sentinel marker short-circuits every call after
    /// the first, the <c>AddOptions</c> call is a no-op on second invocation, and
    /// the backward-bridge <c>TryAddSingleton</c> respects the host's prior descriptor.
    /// </para>
    /// <para>
    /// <b>Recursion safety.</b> The factory / type forward bridges resolve the host's
    /// options via <c>sp.GetRequiredService&lt;TeamsMessagingOptions&gt;()</c> from
    /// inside <see cref="IConfigureOptions{TOptions}"/>. This is safe — no infinite
    /// recursion — because the sentinel guarantees this method runs at most once, so
    /// at SP-build time the host's descriptor wins over the backward-bridge
    /// <c>TryAddSingleton</c>; both surfaces (concrete-type and IOptionsMonitor)
    /// then project from the SAME cached singleton, even when the host's factory is
    /// non-deterministic (e.g. <c>sp =&gt; new TeamsMessagingOptions { MicrosoftAppId = Guid.NewGuid() }</c>).
    /// </para>
    /// </remarks>
    private static void BridgeTeamsMessagingOptions(IServiceCollection services)
    {
        services.AddOptions<TeamsMessagingOptions>();

        // Sentinel-based idempotency guard. After the first bridge installation, the
        // service collection contains both the backward-bridge factory (whose body
        // reads IOptionsMonitor.CurrentValue) and any forward-bridge IConfigureOptions
        // the host's registration triggered. Re-entering this method would treat the
        // backward-bridge factory as the "host" descriptor and register a new
        // IConfigureOptions that reads IOptionsMonitor — re-entering the configure
        // chain and producing either a re-entrancy throw or a stack overflow.
        if (services.Any(d => d.ServiceType == typeof(TeamsMessagingOptionsBridgeMarker)))
        {
            return;
        }
        services.AddSingleton<TeamsMessagingOptionsBridgeMarker>(_ => new TeamsMessagingOptionsBridgeMarker());

        // Snapshot the host-registered descriptor (if any). The reference is captured
        // at registration time; later mutations to the service collection do not
        // retroactively affect the bridge. Hosts that register options AFTER calling
        // AddTeamsSecurity / AddTeamsMessengerConnector intentionally bypass the
        // bridge (matches the standard DI "register options first" guidance).
        var preRegistered = services
            .FirstOrDefault(d => d.ServiceType == typeof(TeamsMessagingOptions));

        if (preRegistered is not null)
        {
            if (preRegistered.ImplementationInstance is TeamsMessagingOptions instance)
            {
                // Pattern A — services.AddSingleton(new TeamsMessagingOptions{...}).
                // Project the captured instance via a plain Configure delegate. No SP
                // needed, no recursion possible.
                services.Configure<TeamsMessagingOptions>(o => CopyTeamsMessagingOptions(instance, o));
            }
            else if (preRegistered.ImplementationFactory is not null
                || preRegistered.ImplementationType is not null)
            {
                // Pattern B — services.AddSingleton<TeamsMessagingOptions>(sp => factory(sp)).
                // Pattern C — services.AddSingleton<TeamsMessagingOptions>() (type-based).
                //
                // Resolve the host's TeamsMessagingOptions through the SP from inside
                // IConfigureOptions. The host's descriptor wins over the backward-bridge
                // TryAddSingleton below, so sp.GetRequiredService<TeamsMessagingOptions>()
                // returns the host's instance and the DI container caches it — both
                // the concrete-type surface and the IOptionsMonitor surface then
                // project from the SAME singleton, eliminating value divergence when
                // the host's factory is non-deterministic.
                services.AddSingleton<IConfigureOptions<TeamsMessagingOptions>>(sp =>
                {
                    var hostInstance = sp.GetRequiredService<TeamsMessagingOptions>();
                    return new ConfigureNamedOptions<TeamsMessagingOptions>(
                        Options.DefaultName,
                        o => CopyTeamsMessagingOptions(hostInstance, o));
                });
            }
        }

        // Backward bridge: when the host wired options via
        // services.Configure<TeamsMessagingOptions>(cfg.GetSection("Teams")) and did
        // NOT also register a concrete singleton, resolve the concrete type from the
        // IOptionsMonitor's CurrentValue. TryAddSingleton ensures the forward-bridge
        // case (where a host singleton is already present) keeps the host's instance —
        // the bridge in that path runs in the OTHER direction (singleton → monitor).
        services.TryAddSingleton<TeamsMessagingOptions>(sp =>
            sp.GetRequiredService<IOptionsMonitor<TeamsMessagingOptions>>().CurrentValue);
    }

    /// <summary>
    /// Sentinel marker registered by <see cref="BridgeTeamsMessagingOptions"/> after
    /// its first run. Subsequent invocations detect this marker and short-circuit
    /// before the host-descriptor inspection logic — preventing the re-entrant
    /// configure loop that would otherwise occur when a host calls
    /// <c>AddTeamsSecurity()</c> multiple times (directly or transitively via
    /// <c>AddTeamsMessengerConnector</c>) with <c>Configure</c>-only options wiring.
    /// </summary>
    private sealed class TeamsMessagingOptionsBridgeMarker
    {
    }

    /// <summary>
    /// Idempotency sentinel for the auto-registered <see cref="TeamsAppPolicyHealthCheck"/>
    /// inside <see cref="AddTeamsSecurity"/>. Presence of this descriptor means the
    /// health-check registration block has already run, so subsequent calls to
    /// <see cref="AddTeamsSecurity"/> short-circuit before adding another
    /// <c>PostConfigure&lt;HealthCheckServiceOptions&gt;</c> delegate. A second source
    /// of duplicate prevention lives inside that delegate itself: it skips appending
    /// when a registration with <see cref="TeamsAppPolicyHealthCheck.Name"/> already
    /// exists, so a host that pre-registered the check with the same canonical name
    /// retains its own registration.
    /// </summary>
    private sealed class TeamsAppPolicyHealthCheckMarker
    {
    }

    /// <summary>
    /// Field-by-field copy used by <see cref="BridgeTeamsMessagingOptions"/> to project
    /// a host-supplied <see cref="TeamsMessagingOptions"/> singleton into the
    /// IOptionsMonitor chain. Kept private + explicit so adding a new field to
    /// <see cref="TeamsMessagingOptions"/> shows up as a compile-time blast-radius
    /// reminder for the bridge (vs reflection-based copy that would silently lose
    /// new fields).
    /// </summary>
    private static void CopyTeamsMessagingOptions(TeamsMessagingOptions source, TeamsMessagingOptions target)
    {
        // Identity fields — required for tenant validation and Entra auth.
        target.MicrosoftAppId = source.MicrosoftAppId;
        target.MicrosoftAppPassword = source.MicrosoftAppPassword;
        target.MicrosoftAppTenantId = source.MicrosoftAppTenantId;
        target.AllowedTenantIds = source.AllowedTenantIds;

        // Operational fields — used by middleware (rate limiting) and notifier (retry).
        target.BotEndpoint = source.BotEndpoint;
        target.RateLimitPerTenantPerMinute = source.RateLimitPerTenantPerMinute;
        target.DeduplicationTtlMinutes = source.DeduplicationTtlMinutes;
        target.ExpiryScanIntervalSeconds = source.ExpiryScanIntervalSeconds;
        target.ExpiryBatchSize = source.ExpiryBatchSize;
        target.MaxRetryAttempts = source.MaxRetryAttempts;
        target.RetryBaseDelaySeconds = source.RetryBaseDelaySeconds;
    }

    /// <summary>
    /// Register <see cref="TeamsAppPolicyHealthCheck"/> with the standard ASP.NET Core
    /// health-check pipeline under <see cref="TeamsAppPolicyHealthCheck.Name"/>. Composes
    /// <see cref="AddTeamsSecurity"/> first, so calling this helper alone is sufficient
    /// to wire the full Stage 5.1 security graph plus the explicit failure-status choice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AddTeamsSecurity"/> already auto-registers the check with
    /// <see cref="HealthStatus.Degraded"/> failure status; this helper exists so hosts can
    /// override that status (e.g. <see cref="HealthStatus.Unhealthy"/> so load balancers
    /// evict the instance when the policy probe fails). The override is performed via
    /// <see cref="OptionsServiceCollectionExtensions.PostConfigure{TOptions}(IServiceCollection, Action{TOptions})"/>
    /// against <see cref="HealthCheckServiceOptions"/> so the host's status replaces the
    /// auto-registered descriptor (matched by canonical <see cref="TeamsAppPolicyHealthCheck.Name"/>),
    /// preventing the runtime "duplicate health check name" startup throw.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="failureStatus">Status returned when the check reports unhealthy. Defaults to <see cref="HealthStatus.Degraded"/>.</param>
    /// <returns>The same <paramref name="services"/> instance (fluent).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddTeamsAppPolicyHealthCheck(
        this IServiceCollection services,
        HealthStatus failureStatus = HealthStatus.Degraded)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.AddTeamsSecurity();

        if (failureStatus != HealthStatus.Degraded)
        {
            services.PostConfigure<HealthCheckServiceOptions>(o =>
            {
                var stale = o.Registrations
                    .Where(r => string.Equals(r.Name, TeamsAppPolicyHealthCheck.Name, StringComparison.Ordinal))
                    .ToList();
                foreach (var registration in stale)
                {
                    o.Registrations.Remove(registration);
                }
                o.Registrations.Add(new HealthCheckRegistration(
                    name: TeamsAppPolicyHealthCheck.Name,
                    factory: sp => sp.GetRequiredService<TeamsAppPolicyHealthCheck>(),
                    failureStatus: failureStatus,
                    tags: new[] { "teams", "security" }));
            });
        }

        return services;
    }

    /// <summary>
    /// Register a Bot Framework <see cref="BotFrameworkAuthentication"/> singleton that
    /// enforces the Stage 5.1 Entra ID restrictions:
    /// <list type="bullet">
    ///   <item><description><c>AllowedCallers</c> — AAD app IDs permitted to call the bot.</description></item>
    ///   <item><description><c>AllowedTenantIds</c> — Entra tenants whose tokens are accepted; populated automatically from <see cref="TeamsMessagingOptions.AllowedTenantIds"/> when not configured explicitly.</description></item>
    /// </list>
    /// The registration replaces any previously-registered
    /// <see cref="BotFrameworkAuthentication"/> descriptor (Stage 2.1 typically registers
    /// the BF SDK's default factory which lacks the tenant / caller restriction) so
    /// downstream <c>CloudAdapter</c> resolutions pick up the hardened authentication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hot-reload contract.</b> The
    /// <see cref="EntraBotFrameworkAuthenticationOptions.AllowedCallers"/> and
    /// <see cref="EntraBotFrameworkAuthenticationOptions.AllowedTenantIds"/> allow-lists
    /// are read by the registered claims validator on EVERY inbound activity (via
    /// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>) — see
    /// <see cref="HotReloadEntraTenantAwareClaimsValidator"/>. This keeps the JWT-layer
    /// validator in lockstep with the HTTP-layer <see cref="TenantValidationMiddleware"/>,
    /// which also reads <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> per request,
    /// so an operator-supplied allow-list update takes effect at both defence-in-depth
    /// layers without a host restart.
    /// </para>
    /// <para>
    /// <b>What does NOT hot-reload (by design):</b>
    /// <see cref="TeamsMessagingOptions.MicrosoftAppId"/>,
    /// <see cref="TeamsMessagingOptions.MicrosoftAppPassword"/>,
    /// <see cref="TeamsMessagingOptions.MicrosoftAppTenantId"/>,
    /// <see cref="EntraBotFrameworkAuthenticationOptions.ChannelService"/>, and
    /// <see cref="EntraBotFrameworkAuthenticationOptions.ValidateAuthority"/> are baked
    /// into the <see cref="BotFrameworkAuthentication"/> singleton at first resolution
    /// because <c>BotFrameworkAuthenticationFactory.Create</c> does not expose a refresh
    /// hook for them. Operators changing those fields MUST restart the host.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configure">Optional configuration delegate for <see cref="EntraBotFrameworkAuthenticationOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> instance (fluent).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddEntraBotFrameworkAuthentication(
        this IServiceCollection services,
        Action<EntraBotFrameworkAuthenticationOptions>? configure = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        var optionsBuilder = services.AddOptions<EntraBotFrameworkAuthenticationOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.RemoveAll<BotFrameworkAuthentication>();
        services.AddSingleton<BotFrameworkAuthentication>(sp =>
        {
            // Hot-reload contract — close the asymmetry between the HTTP-layer
            // TenantValidationMiddleware (which reads _options.CurrentValue per request
            // and observes IConfiguration reloads immediately) and the JWT-layer claims
            // validator. HotReloadEntraTenantAwareClaimsValidator below resolves the
            // option monitors on EVERY ValidateClaimsAsync call so an operator
            // allow-list edit takes effect at both defence-in-depth layers in lockstep
            // without a host restart.
            //
            // The fields snapshotted below (credentials, channel service, validate-
            // authority bool) CANNOT hot-reload because BotFrameworkAuthenticationFactory
            // .Create bakes them into the returned singleton — the BF SDK does not expose
            // a refresh hook. The XML-doc remarks on AddEntraBotFrameworkAuthentication
            // state this contract explicitly for operators.
            var authOptionsMonitor = sp.GetRequiredService<IOptionsMonitor<EntraBotFrameworkAuthenticationOptions>>();
            var messagingOptionsMonitor = sp.GetService<IOptionsMonitor<TeamsMessagingOptions>>();
            var messagingSingletonAccessor = new Func<TeamsMessagingOptions?>(() => sp.GetService<TeamsMessagingOptions>());

            // Snapshot used ONLY for the BF-SDK factory inputs below that cannot
            // hot-reload (credentials, channel service, validate-authority). The
            // allow-lists are NOT read here — the validator reads them per-request.
            var authOptions = authOptionsMonitor.CurrentValue;

            // Resolve TeamsMessagingOptions by trying the concrete singleton FIRST and
            // only falling back to IOptionsMonitor when no singleton is registered. The
            // connector/notifier DI path historically registered TeamsMessagingOptions
            // as a concrete singleton (either via services.AddSingleton(instance) or via
            // a factory); reading the monitor first would return blank defaults for
            // those hosts. BridgeTeamsMessagingOptions in AddTeamsSecurity now also
            // projects instance-registered singletons into the monitor chain, so this
            // double-lookup is belt-and-braces — singleton wins when present, monitor
            // covers the IOptions.Configure-only wiring style.
            var messagingOptions = messagingSingletonAccessor()
                ?? messagingOptionsMonitor?.CurrentValue
                ?? new TeamsMessagingOptions();

            var validator = new HotReloadEntraTenantAwareClaimsValidator(
                authOptionsMonitor: authOptionsMonitor,
                messagingOptionsMonitor: messagingOptionsMonitor,
                messagingSingletonAccessor: messagingSingletonAccessor,
                logger: sp.GetService<ILogger<EntraTenantAwareClaimsValidator>>());

            var authConfig = new AuthenticationConfiguration
            {
                ClaimsValidator = validator,
            };

            var credentials = new PasswordServiceClientCredentialFactory(
                appId: messagingOptions.MicrosoftAppId,
                password: messagingOptions.MicrosoftAppPassword,
                tenantId: messagingOptions.MicrosoftAppTenantId,
                httpClient: null,
                logger: sp.GetService<ILogger<PasswordServiceClientCredentialFactory>>());

            var httpClientFactory = sp.GetService<IHttpClientFactory>();
            var authLogger = sp.GetService<ILogger<BotFrameworkAuthentication>>();

            return BotFrameworkAuthenticationFactory.Create(
                channelService: authOptions.ChannelService ?? string.Empty,
                validateAuthority: authOptions.ValidateAuthority,
                toChannelFromBotLoginUrl: null,
                toChannelFromBotOAuthScope: null,
                toBotFromChannelTokenIssuer: null,
                oAuthUrl: null,
                toBotFromChannelOpenIdMetadataUrl: null,
                toBotFromEmulatorOpenIdMetadataUrl: null,
                callerId: null,
                credentialFactory: credentials,
                authConfiguration: authConfig,
                httpClientFactory: httpClientFactory,
                logger: authLogger);
        });

        return services;
    }
}

/// <summary>
/// Claims-validator adapter that resolves <see cref="EntraBotFrameworkAuthenticationOptions"/>
/// and <see cref="TeamsMessagingOptions"/> on EVERY
/// <see cref="ValidateClaimsAsync(System.Collections.Generic.IList{System.Security.Claims.Claim})"/>
/// call (via the injected <see cref="IOptionsMonitor{TOptions}"/> instances) and
/// constructs a fresh <see cref="EntraTenantAwareClaimsValidator"/> per request from the
/// current allow-lists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this wrapper exists.</b> The
/// <see cref="TenantValidationMiddleware"/> reads
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on every request and therefore
/// observes runtime configuration reloads immediately. If the JWT-layer claims
/// validator snapshotted <see cref="EntraBotFrameworkAuthenticationOptions.AllowedTenantIds"/>
/// and <see cref="EntraBotFrameworkAuthenticationOptions.AllowedCallers"/> at
/// <see cref="BotFrameworkAuthentication"/>-singleton construction time, an operator
/// allow-list edit would leave the JWT layer enforcing the stale lists while the HTTP
/// layer enforced the new lists — silently divergent defence-in-depth posture. This
/// adapter resolves the monitors on every claims-validation call so both layers track
/// configuration changes in lockstep.
/// </para>
/// <para>
/// <b>Why composition (not subclassing).</b> Constructing a fresh
/// <see cref="EntraTenantAwareClaimsValidator"/> per request — rather than reimplementing
/// the validation logic — keeps the validation semantics (including the
/// <see cref="System.StringComparison.OrdinalIgnoreCase"/> tenant comparison that mirrors
/// <see cref="TenantValidationMiddleware"/>'s <c>TenantIdComparison</c>) in a single
/// source of truth. A change to one comparer can no longer drift away from the other.
/// </para>
/// <para>
/// <b>Cost.</b> Each call allocates a single
/// <see cref="EntraTenantAwareClaimsValidator"/> plus its internal short list copy of
/// the allow-lists. This is negligible compared to the JWT-issuer / signature work the
/// Bot Framework SDK already performs around this validation hook.
/// </para>
/// </remarks>
internal sealed class HotReloadEntraTenantAwareClaimsValidator : ClaimsValidator
{
    private readonly IOptionsMonitor<EntraBotFrameworkAuthenticationOptions> _authOptionsMonitor;
    private readonly IOptionsMonitor<TeamsMessagingOptions>? _messagingOptionsMonitor;
    private readonly Func<TeamsMessagingOptions?> _messagingSingletonAccessor;
    private readonly ILogger<EntraTenantAwareClaimsValidator>? _logger;

    /// <summary>Construct a new <see cref="HotReloadEntraTenantAwareClaimsValidator"/>.</summary>
    /// <param name="authOptionsMonitor">Monitor for the Entra auth options whose allow-lists drive validation. REQUIRED.</param>
    /// <param name="messagingOptionsMonitor">Monitor for <see cref="TeamsMessagingOptions"/> used to populate <see cref="EntraBotFrameworkAuthenticationOptions.AllowedTenantIds"/> when the Entra options list is empty. Optional — null when the host does not wire IOptionsMonitor for TeamsMessagingOptions.</param>
    /// <param name="messagingSingletonAccessor">Per-call accessor that returns the host's concrete <see cref="TeamsMessagingOptions"/> singleton when one is registered (takes precedence over the monitor — matches the connector/notifier resolution order). REQUIRED.</param>
    /// <param name="logger">Optional logger forwarded to the inner <see cref="EntraTenantAwareClaimsValidator"/>.</param>
    public HotReloadEntraTenantAwareClaimsValidator(
        IOptionsMonitor<EntraBotFrameworkAuthenticationOptions> authOptionsMonitor,
        IOptionsMonitor<TeamsMessagingOptions>? messagingOptionsMonitor,
        Func<TeamsMessagingOptions?> messagingSingletonAccessor,
        ILogger<EntraTenantAwareClaimsValidator>? logger)
    {
        _authOptionsMonitor = authOptionsMonitor ?? throw new ArgumentNullException(nameof(authOptionsMonitor));
        _messagingSingletonAccessor = messagingSingletonAccessor ?? throw new ArgumentNullException(nameof(messagingSingletonAccessor));
        _messagingOptionsMonitor = messagingOptionsMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task ValidateClaimsAsync(IList<System.Security.Claims.Claim> claims)
    {
        if (claims is null) throw new ArgumentNullException(nameof(claims));

        var authOptions = _authOptionsMonitor.CurrentValue;
        var messagingOptions = _messagingSingletonAccessor()
            ?? _messagingOptionsMonitor?.CurrentValue
            ?? new TeamsMessagingOptions();

        var allowedTenants = authOptions.AllowedTenantIds is { Count: > 0 }
            ? authOptions.AllowedTenantIds
            : messagingOptions.AllowedTenantIds ?? new List<string>();
        var allowedCallers = authOptions.AllowedCallers ?? new List<string>();

        var inner = new EntraTenantAwareClaimsValidator(
            allowedCallers: allowedCallers,
            allowedTenantIds: allowedTenants,
            requireTenantClaim: authOptions.RequireTenantClaim,
            logger: _logger);

        return inner.ValidateClaimsAsync(claims);
    }
}
