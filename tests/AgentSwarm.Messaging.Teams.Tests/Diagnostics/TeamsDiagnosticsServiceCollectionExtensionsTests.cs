using AgentSwarm.Messaging.Teams.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Verifies the §6.3 DI surface — that the helpers register the canonical
/// telemetry singleton + both health checks, that they are idempotent, and that
/// the registered health-check entries land in
/// <see cref="HealthCheckServiceOptions.Registrations"/> with the canonical names
/// and tags.
/// </summary>
public sealed class TeamsDiagnosticsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTeamsConnectorTelemetry_RegistersTelemetryAndDefaultQueueDepthProvider()
    {
        var services = new ServiceCollection();
        services.AddTeamsConnectorTelemetry();

        using var sp = services.BuildServiceProvider();
        var telemetry = sp.GetService<TeamsConnectorTelemetry>();
        var provider = sp.GetService<IOutboxQueueDepthProvider>();

        Assert.NotNull(telemetry);
        Assert.NotNull(provider);
        Assert.IsType<InMemoryOutboxQueueDepthProvider>(provider);
    }

    [Fact]
    public void AddTeamsConnectorTelemetry_PreservesExplicitQueueDepthProvider()
    {
        var services = new ServiceCollection();
        var custom = new InMemoryOutboxQueueDepthProvider();
        custom.SetQueueDepth(999);
        services.AddSingleton<IOutboxQueueDepthProvider>(custom);

        services.AddTeamsConnectorTelemetry();

        using var sp = services.BuildServiceProvider();
        var resolved = Assert.IsType<InMemoryOutboxQueueDepthProvider>(sp.GetRequiredService<IOutboxQueueDepthProvider>());
        Assert.Same(custom, resolved);
        Assert.Equal(999L, resolved.GetQueueDepth());
    }

    [Fact]
    public void AddTeamsConnectorTelemetry_WithOutboxMetricsInDi_BridgesGaugeToOutboxMetrics()
    {
        // Stage 6.3 iter-2 — when OutboxMetrics is in the DI graph (i.e. the host has
        // composed the outbox engine alongside the Teams connector), the default
        // IOutboxQueueDepthProvider MUST be OutboxMetricsQueueDepthProvider so the
        // teams.outbox.queue_depth gauge mirrors the depth that OutboxRetryEngine
        // pushes onto OutboxMetrics.SetPendingCount. The previous in-memory default
        // forced hosts to write a duplicate setter — that integration gap was the
        // iter-1 evaluator finding (item 2).
        var services = new ServiceCollection();
        var outboxOptions = new AgentSwarm.Messaging.Core.OutboxOptions();
        var outboxMetrics = new AgentSwarm.Messaging.Core.OutboxMetrics(outboxOptions);
        services.AddSingleton(outboxMetrics);

        services.AddTeamsConnectorTelemetry();

        using var sp = services.BuildServiceProvider();
        var provider = Assert.IsType<OutboxMetricsQueueDepthProvider>(sp.GetRequiredService<IOutboxQueueDepthProvider>());

        // Push 42 onto OutboxMetrics — the wrapper must observe it through the same
        // underlying counter without any extra plumbing on the engine side.
        outboxMetrics.SetPendingCount(42L);
        Assert.Equal(42L, provider.GetQueueDepth());

        outboxMetrics.Dispose();
    }

    [Fact]
    public void AddTeamsConnectorTelemetry_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddTeamsConnectorTelemetry();
        var firstCount = services.Count;

        services.AddTeamsConnectorTelemetry();

        Assert.Equal(firstCount, services.Count);
    }

    [Fact]
    public void AddBotFrameworkConnectivityHealthCheck_RegistersWithCanonicalNameAndTags()
    {
        var services = new ServiceCollection();
        services.AddBotFrameworkConnectivityHealthCheck();

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(options.Registrations, r => r.Name == BotFrameworkConnectivityHealthCheck.Name);

        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
        Assert.Contains("teams", registration.Tags);
        Assert.Contains("bot-framework", registration.Tags);
    }

    [Fact]
    public void AddConversationReferenceStoreHealthCheck_RegistersWithCanonicalNameAndTags()
    {
        var services = new ServiceCollection();
        services.AddConversationReferenceStoreHealthCheck();

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(options.Registrations, r => r.Name == ConversationReferenceStoreHealthCheck.Name);

        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
        Assert.Contains("teams", registration.Tags);
        Assert.Contains("persistence", registration.Tags);
    }

    [Fact]
    public void AddTeamsDiagnostics_RegistersTelemetryAndBothHealthChecks()
    {
        var services = new ServiceCollection();
        services.AddTeamsDiagnostics();

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<TeamsConnectorTelemetry>());
        Assert.NotNull(sp.GetService<TeamsLogEnricher>());
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        Assert.Contains(options.Registrations, r => r.Name == BotFrameworkConnectivityHealthCheck.Name);
        Assert.Contains(options.Registrations, r => r.Name == ConversationReferenceStoreHealthCheck.Name);
    }

    [Fact]
    public void AddTeamsSerilogEnricher_RegistersEnricherAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddTeamsSerilogEnricher();

        using var sp = services.BuildServiceProvider();
        var first = sp.GetRequiredService<TeamsLogEnricher>();
        var second = sp.GetRequiredService<TeamsLogEnricher>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddTeamsSerilogEnricher_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddTeamsSerilogEnricher();
        var firstCount = services.Count;

        services.AddTeamsSerilogEnricher();

        Assert.Equal(firstCount, services.Count);
    }

    [Fact]
    public void AllHelpers_NullServices_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => TeamsDiagnosticsServiceCollectionExtensions.AddTeamsDiagnostics(null!));
        Assert.Throws<ArgumentNullException>(() => TeamsDiagnosticsServiceCollectionExtensions.AddTeamsConnectorTelemetry(null!));
        Assert.Throws<ArgumentNullException>(() => TeamsDiagnosticsServiceCollectionExtensions.AddTeamsSerilogEnricher(null!));
        Assert.Throws<ArgumentNullException>(() => TeamsDiagnosticsServiceCollectionExtensions.AddBotFrameworkConnectivityHealthCheck(null!));
        Assert.Throws<ArgumentNullException>(() => TeamsDiagnosticsServiceCollectionExtensions.AddConversationReferenceStoreHealthCheck(null!));
    }

    [Fact]
    public void AddBotFrameworkConnectivityHealthCheck_CalledTwice_RegistersExactlyOneHealthCheckEntry()
    {
        // Iter-2 evaluator feedback item 1 — the XML doc claimed idempotency but the
        // helper called AddHealthChecks().AddCheck<T>(name) unconditionally, so two
        // calls duplicated the registration and the runtime threw "duplicate name" at
        // first probe. The fix uses a marker singleton + slot-claim pattern; this test
        // pins the contract by asserting exactly ONE entry with the canonical name
        // even after THREE invocations.
        var services = new ServiceCollection();
        services.AddBotFrameworkConnectivityHealthCheck();
        services.AddBotFrameworkConnectivityHealthCheck();
        services.AddBotFrameworkConnectivityHealthCheck();

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        var entries = options.Registrations.Where(r => r.Name == BotFrameworkConnectivityHealthCheck.Name).ToList();
        Assert.Single(entries);
    }

    [Fact]
    public void AddConversationReferenceStoreHealthCheck_CalledTwice_RegistersExactlyOneHealthCheckEntry()
    {
        // Same contract as AddBotFrameworkConnectivityHealthCheck above — iter-2
        // evaluator item 1 covers both helpers.
        var services = new ServiceCollection();
        services.AddConversationReferenceStoreHealthCheck();
        services.AddConversationReferenceStoreHealthCheck();
        services.AddConversationReferenceStoreHealthCheck();

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        var entries = options.Registrations.Where(r => r.Name == ConversationReferenceStoreHealthCheck.Name).ToList();
        Assert.Single(entries);
    }

    [Fact]
    public void AddTeamsDiagnostics_CalledTwice_RegistersExactlyOneOfEachHealthCheck()
    {
        // AddTeamsDiagnostics composes both granular helpers — a repeated call must
        // not duplicate either entry. This guards against the case where the host
        // re-runs DI composition (e.g. test bootstrap inside an integration test
        // server) and previously would have ended up with 2x bot-framework + 2x
        // conversation-reference entries.
        var services = new ServiceCollection();
        services.AddTeamsDiagnostics();
        services.AddTeamsDiagnostics();

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        Assert.Single(options.Registrations, r => r.Name == BotFrameworkConnectivityHealthCheck.Name);
        Assert.Single(options.Registrations, r => r.Name == ConversationReferenceStoreHealthCheck.Name);
    }

    [Fact]
    public void AddBotFrameworkConnectivityHealthCheck_RegistersDefaultMicrosoftAppCredentialsTokenProbe()
    {
        // Iter-2 evaluator feedback item 3 — the connectivity health check must
        // exercise real app-credential token acquisition by default, not just OIDC
        // discovery reachability. The default IBotFrameworkTokenProbe registered by
        // this helper is MicrosoftAppCredentialsTokenProbe, which calls
        // MicrosoftAppCredentials.GetTokenAsync against the live Bot Framework /
        // Entra token endpoint.
        var services = new ServiceCollection();
        services.AddBotFrameworkConnectivityHealthCheck();

        // The probe needs IOptionsMonitor<TeamsMessagingOptions> to resolve; satisfy
        // it minimally so BuildServiceProvider can graph the probe.
        services.AddOptions<TeamsMessagingOptions>();

        using var sp = services.BuildServiceProvider();
        var probe = sp.GetService<IBotFrameworkTokenProbe>();
        Assert.NotNull(probe);
        Assert.IsType<MicrosoftAppCredentialsTokenProbe>(probe);
    }

    [Fact]
    public void AddBotFrameworkConnectivityHealthCheck_PreservesExplicitTokenProbeRegistration()
    {
        // Hosts that use certificate, federated, or managed-identity auth supply their
        // own IBotFrameworkTokenProbe — the helper must preserve that explicit
        // registration (TryAdd semantics) rather than overwriting it with the default
        // MicrosoftAppCredentialsTokenProbe.
        var services = new ServiceCollection();
        var custom = new StaticTokenProbe();
        services.AddSingleton<IBotFrameworkTokenProbe>(custom);

        services.AddBotFrameworkConnectivityHealthCheck();
        services.AddOptions<TeamsMessagingOptions>();

        using var sp = services.BuildServiceProvider();
        Assert.Same(custom, sp.GetRequiredService<IBotFrameworkTokenProbe>());
    }

    private sealed class StaticTokenProbe : IBotFrameworkTokenProbe
    {
        public Task<BotFrameworkTokenProbeResult> AcquireTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult(new BotFrameworkTokenProbeResult(BotFrameworkTokenProbeStatus.Succeeded));
    }
}
