// -----------------------------------------------------------------------
// <copyright file="SlackMessengerServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Diagnostics;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 8.1 acceptance test (Scenario: "Full DI container builds"):
/// <c>Given <see cref="SlackMessengerServiceCollectionExtensions.AddSlackMessenger(IServiceCollection, IConfiguration)"/>
/// is called with valid configuration, When the DI container is built,
/// Then <see cref="IMessengerConnector"/> resolves to
/// <see cref="SlackConnector"/> and all internal dependencies are
/// satisfied.</c>
/// </summary>
/// <remarks>
/// <para>
/// Pure-DI test: builds a minimal <see cref="ServiceCollection"/> with
/// just the cross-platform prerequisites (logging, the in-memory secret
/// provider, an in-memory <see cref="SlackPersistenceDbContext"/>) and
/// invokes the facade. The host-level HTTP pipeline (controllers,
/// signature middleware, health-endpoint mapping, EnsureCreated)
/// requires <see cref="Microsoft.AspNetCore.Builder.WebApplication"/>
/// and is exercised by
/// <see cref="AgentSwarm.Messaging.Slack.Tests.Security.WorkerSlackSignatureCompositionTests"/>;
/// keeping this test pure-DI lets it pin the facade contract
/// independently of the Worker composition root so a future regression
/// in <see cref="SlackMessengerServiceCollectionExtensions"/> surfaces
/// here without dragging the Worker host through SQLite bootstrap.
/// </para>
/// </remarks>
public sealed class SlackMessengerServiceCollectionExtensionsTests
{
    private const string TeamId = "T-STAGE-8-1";

    [Fact]
    public void AddSlackMessenger_resolves_IMessengerConnector_to_SlackConnector()
    {
        // Arrange
        ServiceProvider provider = BuildFacadeContainer();

        // Act
        IMessengerConnector connector = provider.GetRequiredService<IMessengerConnector>();

        // Assert -- the canonical Slack implementation of the
        // platform-neutral IMessengerConnector contract.
        connector.Should().NotBeNull();
        connector.Should().BeOfType<SlackConnector>(
            "Stage 8.1 requires the facade to bind SlackConnector as the IMessengerConnector implementation so a host that polls every registered connector uniformly sees Slack");
    }

    [Fact]
    public void AddSlackMessenger_satisfies_every_SlackConnector_constructor_dependency()
    {
        // Arrange
        ServiceProvider provider = BuildFacadeContainer();

        // Act / Assert -- each constructor dependency of
        // SlackConnector must resolve from DI. Resolving them
        // individually pins the contract so a future refactor that
        // forgets a single registration (e.g. drops
        // ISlackInboundEventBuffer) surfaces here with a precise
        // failure rather than the opaque
        // "InvalidOperationException: Unable to resolve service"
        // from the IMessengerConnector lookup.
        provider.GetRequiredService<ISlackOutboundQueue>()
            .Should().NotBeNull("SlackConnector enqueues outbound envelopes through the queue");
        provider.GetRequiredService<ISlackThreadManager>()
            .Should().NotBeNull("SlackConnector resolves/creates per-task threads via the manager");
        provider.GetRequiredService<ISlackMessageRenderer>()
            .Should().NotBeNull("SlackConnector renders MessengerMessage / AgentQuestion into Block Kit");
        provider.GetRequiredService<IOptionsMonitor<SlackOutboundOptions>>()
            .Should().NotBeNull("SlackConnector reads DefaultTeamId from the outbound options");
        provider.GetRequiredService<ISlackInboundEventBuffer>()
            .Should().NotBeNull("SlackConnector.ReceiveAsync drains processed events from the buffer");
    }

    [Fact]
    public void AddSlackMessenger_registers_every_internal_pipeline_collaborator()
    {
        // Arrange
        ServiceProvider provider = BuildFacadeContainer();

        // Act / Assert -- the broader set of internal handlers /
        // guards / transports / renderers / audit + signature
        // primitives the Stage 8.1 brief calls for. A test that
        // resolves all of them in one shot pins the
        // AddSlackMessenger surface against accidental sub-extension
        // removal.

        // Renderer / outbound transport.
        provider.GetRequiredService<ISlackMessageRenderer>().Should().NotBeNull();
        provider.GetRequiredService<ISlackOutboundDispatchClient>().Should().NotBeNull();
        provider.GetRequiredService<ISlackChatPostMessageClient>().Should().NotBeNull();
        provider.GetRequiredService<ISlackChatUpdateClient>().Should().NotBeNull();
        provider.GetRequiredService<ISlackEphemeralResponder>().Should().NotBeNull();
        provider.GetRequiredService<ISlackThreadedReplyPoster>().Should().NotBeNull();
        provider.GetRequiredService<ISlackRateLimiter>().Should().NotBeNull();
        provider.GetRequiredService<ISlackRetryPolicy>().Should().NotBeNull();

        // Queues / DLQs.
        provider.GetRequiredService<ISlackOutboundQueue>().Should().NotBeNull();
        provider.GetRequiredService<ISlackDeadLetterQueue>().Should().NotBeNull();
        provider.GetRequiredService<ISlackInboundQueue>().Should().NotBeNull();
        provider.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>().Should().NotBeNull();

        // Inbound processing.
        provider.GetRequiredService<ISlackInboundAuthorizer>().Should().NotBeNull();
        provider.GetRequiredService<ISlackIdempotencyGuard>().Should().NotBeNull();
        provider.GetRequiredService<ISlackThreadMappingLookup>().Should().NotBeNull();
        provider.GetRequiredService<ISlackThreadManager>().Should().NotBeNull();
        provider.GetRequiredService<ISlackInteractionFastPathHandler>().Should().NotBeNull();
        provider.GetRequiredService<ISlackInboundEventBuffer>().Should().NotBeNull();
        provider.GetRequiredService<IAgentTaskService>()
            .Should().BeOfType<BufferingAgentTaskServiceDecorator>(
                "Stage 8.1 wraps the orchestrator with the buffering decorator so HumanDecisionEvents land in the inbound buffer SlackConnector.ReceiveAsync drains");

        // Security.
        provider.GetRequiredService<SlackSignatureValidator>().Should().NotBeNull();
        provider.GetRequiredService<ISlackSignatureAuditSink>().Should().NotBeNull();
        provider.GetRequiredService<SlackAuthorizationFilter>().Should().NotBeNull();
        provider.GetRequiredService<ISlackMembershipResolver>().Should().NotBeNull();

        // Persistence + audit.
        provider.GetRequiredService<ISlackAuditEntryWriter>().Should().NotBeNull();
        provider.GetRequiredService<ISlackAuditLogger>().Should().NotBeNull();
        provider.GetRequiredService<ISlackWorkspaceConfigStore>().Should().NotBeNull();

        // Transport: SlackDirectApiClient (views.open) is exposed
        // through ISlackViewsOpenClient.
        provider.GetRequiredService<ISlackViewsOpenClient>().Should().NotBeNull(
            "Stage 8.1 step 5 explicitly calls out registering the SlackDirectApiClient");

        // Iter-4 evaluator items 1 + 3: the facade composes the
        // Stage 4.2 Socket Mode transport unconditionally. The host's
        // per-workspace SlackInboundTransportFactory chooses HTTP
        // (Events API) vs WebSocket (Socket Mode) at runtime based
        // on each workspace's AppLevelTokenRef, so BOTH transports
        // must be wired regardless of whether any currently-seeded
        // workspace uses Socket Mode. Resolving these here pins the
        // Stage 8.1 contract that AddSlackMessenger registers "all
        // transports" per the implementation plan.
        provider.GetRequiredService<ISlackInboundTransportFactory>().Should().NotBeNull(
            "Stage 8.1 + iter-4 item 1: per-workspace transport selector for HTTP vs Socket Mode");
        provider.GetRequiredService<ISlackSocketModeConnectionFactory>().Should().NotBeNull(
            "Stage 4.2 Socket Mode WebSocket connection factory");
        provider.GetRequiredService<SlackSocketModeOptions>().Should().NotBeNull(
            "Stage 4.2 SlackSocketModeOptions value-instance (eagerly bound from Slack:SocketMode)");

        // Options shapes.
        provider.GetRequiredService<IOptions<SlackConnectorOptions>>().Value.Should().NotBeNull();
        provider.GetRequiredService<IOptions<SlackSignatureOptions>>().Value.Should().NotBeNull();
        provider.GetRequiredService<IOptions<SlackInboundEventBufferOptions>>().Value.Should().NotBeNull();
        provider.GetRequiredService<IOptions<SlackSocketModeOptions>>().Value.Should().NotBeNull(
            "Stage 4.2 SlackSocketModeOptions reachable via IOptions<T> for downstream consumers");
        provider.GetRequiredService<IOptionsMonitor<SlackOutboundOptions>>().CurrentValue.Should().NotBeNull();
    }

    [Fact]
    public void AddSlackMessenger_is_idempotent()
    {
        // Arrange: build a service collection, run AddSlackMessenger
        // twice with identical configuration, and verify the second
        // invocation does not break resolution. Idempotency is a
        // documented contract -- composition roots that compose the
        // facade and then a host-level test fixture that ALSO calls
        // the facade must not double-register and crash on
        // GetRequiredService.
        IConfiguration configuration = BuildFacadeConfiguration();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<SlackPersistenceDbContext>(opts =>
            opts.UseSqlite("Data Source=:memory:"));
        services.AddMessagingCore(configuration);
        services.AddMessagingPersistence(configuration);
        services.AddSecretProvider(configuration);
        // Iter-3 item 2: the facade no longer auto-registers a NoOp
        // IAgentTaskService. Tests opt in explicitly via the dev
        // defaults so the production guard is unaffected.
        services.AddSlackCommandDispatcherDevelopmentDefaults();

        // First invocation.
        services.AddSlackMessenger(configuration);

        // Second invocation -- the facade documents itself as
        // idempotent.
        services.AddSlackMessenger(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            IMessengerConnector connector = provider.GetRequiredService<IMessengerConnector>();
            connector.Should().BeOfType<SlackConnector>(
                "double-invoking AddSlackMessenger must not break the IMessengerConnector resolution");
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public async Task AddSlackMessenger_does_not_double_decorate_IAgentTaskService()
    {
        // Stage 8.1 iter-2 item 2: regression test pinning the
        // structural fix for the "decorator chain grows on every
        // facade call" defect. Calling AddSlackMessenger N times
        // must wrap BufferingAgentTaskServiceDecorator around the
        // host's IAgentTaskService EXACTLY ONCE so a single
        // PublishDecisionAsync produces exactly one entry in the
        // ISlackInboundEventBuffer (and therefore exactly one
        // MessengerEvent on the next SlackConnector.ReceiveAsync
        // poll). Without the idempotency guard the count grows
        // linearly with the number of facade invocations.
        IConfiguration configuration = BuildFacadeConfiguration();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<SlackPersistenceDbContext>(opts =>
            opts.UseSqlite("Data Source=:memory:"));
        services.AddMessagingCore(configuration);
        services.AddMessagingPersistence(configuration);
        services.AddSecretProvider(configuration);
        // Iter-3 item 2: explicit dev-defaults opt-in (the facade no
        // longer auto-installs the NoOp stub).
        services.AddSlackCommandDispatcherDevelopmentDefaults();

        // Invoke the facade three times to make the regression
        // surface (any layered wrap would push 3 events per
        // decision).
        services.AddSlackMessenger(configuration);
        services.AddSlackMessenger(configuration);
        services.AddSlackMessenger(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        IAgentTaskService taskService = provider.GetRequiredService<IAgentTaskService>();
        ISlackInboundEventBuffer buffer = provider.GetRequiredService<ISlackInboundEventBuffer>();
        IMessengerConnector connector = provider.GetRequiredService<IMessengerConnector>();

        taskService.Should().BeOfType<BufferingAgentTaskServiceDecorator>(
            "the facade wraps the IAgentTaskService with the buffering decorator");

        HumanDecisionEvent decision = new(
            QuestionId: "Q-IDEMPOT-1",
            ActionValue: "approve",
            Comment: null,
            Messenger: "slack",
            ExternalUserId: "U-1",
            ExternalMessageId: "1700000000.000001",
            ReceivedAt: DateTimeOffset.UtcNow,
            CorrelationId: "corr-idempot-1");

        await taskService.PublishDecisionAsync(decision, CancellationToken.None);

        buffer.Count.Should().Be(1,
            "a single PublishDecisionAsync MUST push exactly one event into the buffer, regardless of how many times AddSlackMessenger was invoked");

        IReadOnlyList<MessengerEvent> drained = await connector.ReceiveAsync(CancellationToken.None);
        drained.Should().HaveCount(1,
            "and the connector poll surfaces it exactly once");
    }

    [Fact]
    public void AddSlackMessenger_throws_when_SlackInboundIngestor_hosted_service_is_missing()
    {
        // Stage 8.1 iter-2 item 1: the facade asserts both hosted
        // service peers (ingestor + dispatcher) are present at
        // facade build-time. Simulate a corrupted composition where
        // only the dispatcher hosted-service is registered and the
        // ingestor descriptor was removed -- the validator MUST
        // raise an InvalidOperationException naming the missing
        // component so the operator sees the failure synchronously
        // rather than as a dropped-message silence at runtime.
        ServiceCollection services = new();

        // Register a descriptor pointing at the real
        // SlackOutboundDispatcher type (the validator only inspects
        // descriptors, never constructs them, so the heavy dep graph
        // is irrelevant). Omit SlackInboundIngestor entirely.
        services.Add(new ServiceDescriptor(
            typeof(IHostedService),
            typeof(SlackOutboundDispatcher),
            ServiceLifetime.Singleton));

        Action act = () =>
            SlackMessengerServiceCollectionExtensions
                .ValidateConnectorHostedServiceComposition(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SlackInboundIngestor*not registered*");
    }

    [Fact]
    public void AddSlackMessenger_throws_when_SlackOutboundDispatcher_hosted_service_is_missing()
    {
        // Stage 8.1 iter-2 item 1: symmetric guard for the
        // outbound dispatcher.
        ServiceCollection services = new();
        services.Add(new ServiceDescriptor(
            typeof(IHostedService),
            typeof(SlackInboundIngestor),
            ServiceLifetime.Singleton));

        Action act = () =>
            SlackMessengerServiceCollectionExtensions
                .ValidateConnectorHostedServiceComposition(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SlackOutboundDispatcher*not registered*");
    }

    [Fact]
    public void AddSlackMessenger_throws_when_both_hosted_services_are_missing()
    {
        // Empty collection -- the validator reports the ingestor
        // first (the order matches how a host typically wires
        // inbound BEFORE outbound). Either error message satisfies
        // the contract, but the test pins the ingestor-first order
        // so a future re-ordering surfaces here.
        ServiceCollection services = new();

        Action act = () =>
            SlackMessengerServiceCollectionExtensions
                .ValidateConnectorHostedServiceComposition(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SlackInboundIngestor*");
    }

    [Fact]
    public void AddSlackMessenger_validates_with_ValidateOnBuild_and_ValidateScopes()
    {
        // Stage 8.1 iter-2 item 3: the acceptance test MUST exercise
        // the strict DI validation modes so a constructor regression
        // in ANY registered service (not just the ones the test
        // explicitly resolves) surfaces here. ValidateOnBuild walks
        // the entire descriptor list and ValidateScopes catches
        // captive-dependency bugs.
        IConfiguration configuration = BuildFacadeConfiguration();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<SlackPersistenceDbContext>(opts =>
            opts.UseSqlite("Data Source=:memory:"));
        services.AddMessagingCore(configuration);
        services.AddMessagingPersistence(configuration);
        services.AddSecretProvider(configuration);
        // Iter-3 item 2: explicit dev-defaults opt-in (the facade no
        // longer auto-installs the NoOp orchestrator stub).
        services.AddSlackCommandDispatcherDevelopmentDefaults();
        services.AddSlackMessenger(configuration);

        Action act = () => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }).Dispose();

        act.Should().NotThrow(
            "every service AddSlackMessenger registers MUST be constructible by DI without captive-dependency or missing-dependency errors");
    }

    [Fact]
    public void AddSlackMessenger_constructs_every_hosted_service_including_ingestor_and_dispatcher()
    {
        // Stage 8.1 iter-2 item 3: the prior test resolved a handful
        // of named services; this one resolves the IEnumerable<IHostedService>
        // and asserts that every hosted service registered by the
        // facade actually constructs. A constructor regression in
        // SlackInboundIngestor, SlackOutboundDispatcher, or any
        // other hosted peer would surface here even if no test
        // names that service by type.
        using ServiceProvider provider = BuildFacadeContainer();

        IEnumerable<IHostedService> hostedServices = provider.GetServices<IHostedService>();
        IReadOnlyList<IHostedService> materialized = hostedServices.ToList();

        materialized.Should().NotBeEmpty();
        materialized.OfType<SlackInboundIngestor>().Should().HaveCount(1,
            "Stage 8.1 step 1 requires the SlackInboundIngestor to be wired as an IHostedService so inbound envelopes are drained");
        materialized.OfType<SlackOutboundDispatcher>().Should().HaveCount(1,
            "Stage 8.1 step 1 requires the SlackOutboundDispatcher to be wired as an IHostedService so outbound envelopes are delivered");
        materialized.OfType<SlackInboundTransportHostedService>().Should().HaveCount(1,
            "Stage 8.1 iter-4 item 3 + Stage 4.2: the facade now wires AddSlackSocketModeTransport, "
            + "which registers SlackInboundTransportHostedService as the BackgroundService that actually "
            + "starts per-workspace Socket Mode receivers on host boot. Without this hosted service the "
            + "SlackSocketModeReceiver classes exist in the container but no Slack workspace ever connects.");
    }

    [Fact]
    public void AddSlackMessenger_binds_default_OverflowPolicy_to_DropNewest()
    {
        // Stage 8.1 iter-2 item 4: the structural fix changes the
        // default policy from DropOldest (legacy) to DropNewest so
        // a stalled poller eventually surfaces the oldest events
        // once it catches up. The default flowing through to the
        // bound options snapshot is what hosts depend on.
        using ServiceProvider provider = BuildFacadeContainer();

        SlackInboundEventBufferOptions bufferOpts = provider
            .GetRequiredService<IOptions<SlackInboundEventBufferOptions>>().Value;

        bufferOpts.OverflowPolicy.Should().Be(
            SlackInboundEventBufferOverflowPolicy.DropNewest,
            "the iter-2 default preserves the oldest never-drained events for a stalled poller");
    }

    [Fact]
    public void AddSlackMessenger_rejects_null_services()
    {
        IConfiguration cfg = BuildFacadeConfiguration();

        Action act = () => SlackMessengerServiceCollectionExtensions.AddSlackMessenger(null!, cfg);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("services");
    }

    [Fact]
    public void AddSlackMessenger_rejects_null_configuration()
    {
        ServiceCollection services = new();

        Action act = () => services.AddSlackMessenger(null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("configuration");
    }

    [Fact]
    public void AddSlackMessenger_throws_when_no_IAgentTaskService_is_registered()
    {
        // Stage 8.1 iter-3 item 2: the facade NO LONGER auto-installs
        // NoOpAgentTaskService. A composition root that forgets both
        // the real orchestrator AND the explicit dev opt-in MUST fail
        // fast inside AddSlackMessenger with a precise remediation
        // message; the previous behaviour silently registered the
        // NoOp stub and let production hosts ACK /agent ask requests
        // with synthetic tasks that never ran real swarm work.
        IConfiguration configuration = BuildFacadeConfiguration();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<SlackPersistenceDbContext>(opts =>
            opts.UseSqlite("Data Source=:memory:"));
        services.AddMessagingCore(configuration);
        services.AddMessagingPersistence(configuration);
        services.AddSecretProvider(configuration);
        // Deliberately DO NOT register an IAgentTaskService -- no
        // real orchestrator, no explicit dev-defaults opt-in.

        Action act = () => services.AddSlackMessenger(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no IAgentTaskService*is registered*",
                "the facade build-time guard MUST reject a composition with no orchestrator implementation");
    }

    [Fact]
    public void AddSlackMessenger_accepts_dev_NoOp_when_explicitly_opted_in()
    {
        // Sister test to the negative case: the explicit dev-defaults
        // opt-in (the Worker's current path) satisfies the guard, so
        // the facade succeeds and binds the NoOp behind the buffering
        // decorator just like a real orchestrator would be.
        IConfiguration configuration = BuildFacadeConfiguration();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<SlackPersistenceDbContext>(opts =>
            opts.UseSqlite("Data Source=:memory:"));
        services.AddMessagingCore(configuration);
        services.AddMessagingPersistence(configuration);
        services.AddSecretProvider(configuration);
        services.AddSlackCommandDispatcherDevelopmentDefaults();

        Action act = () => services.AddSlackMessenger(configuration);

        act.Should().NotThrow(
            "the explicit dev-defaults opt-in is the supported development path the Worker uses today");
    }

    [Fact]
    public void AddSlackMessenger_accepts_real_orchestrator_pre_registered()
    {
        // The production path: a host that registers a real
        // IAgentTaskService BEFORE the facade satisfies the guard
        // (no dev defaults required). The decorator wraps the real
        // implementation, preserving its lifetime.
        IConfiguration configuration = BuildFacadeConfiguration();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<SlackPersistenceDbContext>(opts =>
            opts.UseSqlite("Data Source=:memory:"));
        services.AddMessagingCore(configuration);
        services.AddMessagingPersistence(configuration);
        services.AddSecretProvider(configuration);
        services.AddSingleton<IAgentTaskService, RealOrchestratorStub>();

        Action act = () => services.AddSlackMessenger(configuration);

        act.Should().NotThrow(
            "a host that registered a real orchestrator BEFORE the facade satisfies the guard without opting into the dev stub");

        using ServiceProvider provider = services.BuildServiceProvider();
        IAgentTaskService resolved = provider.GetRequiredService<IAgentTaskService>();
        resolved.Should().BeOfType<BufferingAgentTaskServiceDecorator>(
            "the facade still wraps the real orchestrator with the buffering decorator");
    }

    private static ServiceProvider BuildFacadeContainer()
    {
        IConfiguration configuration = BuildFacadeConfiguration();
        ServiceCollection services = new();

        // Bare-minimum host services the facade depends on.
        services.AddLogging();

        // The facade documents that the host owns the DbContext
        // registration so the Slack assembly stays decoupled from any
        // specific EF provider. Use an in-memory SQLite so the test
        // exercises the SAME EF wiring code-path the Worker host
        // uses, without writing to disk.
        services.AddDbContext<SlackPersistenceDbContext>(opts =>
            opts.UseSqlite("Data Source=:memory:"));

        // Cross-platform primitives (Stage 8.1 brief step 6).
        services.AddMessagingCore(configuration);
        services.AddMessagingPersistence(configuration);
        services.AddSecretProvider(configuration);

        // Iter-3 item 2: the facade no longer auto-installs a NoOp
        // IAgentTaskService stub. The acceptance tests mirror the
        // Worker's composition root by explicitly opting in to the
        // dev-defaults shim BEFORE invoking AddSlackMessenger.
        services.AddSlackCommandDispatcherDevelopmentDefaults();

        // The facade under test.
        services.AddSlackMessenger(configuration);

        // Stage 8.1 iter-2 item 3: ValidateOnBuild + ValidateScopes
        // catch constructor regressions in EVERY registered service,
        // not just the ones the test resolves by hand. Pair with the
        // explicit hosted-service resolution test below for the
        // strongest acceptance signal.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static IConfiguration BuildFacadeConfiguration()
    {
        // Minimum valid Slack configuration: a DefaultTeamId so
        // SlackConnector.ResolveTeamId does not throw, an in-memory
        // secret provider so the signature validator's
        // SlackWorkspaceSecretRefSource can resolve, and the dead-letter
        // / journal directories pointed at a per-test temp dir so two
        // parallel test instances do not collide.
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "qq-stage-8.1-facade-" + Guid.NewGuid().ToString("N"));

        Dictionary<string, string?> overrides = new()
        {
            ["SecretProvider:ProviderType"] = "InMemory",
            ["Slack:Outbound:DefaultTeamId"] = TeamId,
            ["Slack:Inbound:DeadLetterDirectory"] = Path.Combine(tempRoot, "inbound-dlq"),
            ["Slack:Outbound:JournalDirectory"] = Path.Combine(tempRoot, "outbound-journal"),
            ["Slack:Outbound:DeadLetterDirectory"] = Path.Combine(tempRoot, "outbound-dlq"),
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(overrides)
            .Build();
    }

    /// <summary>
    /// Fake hosted service kept around in case future tests need a
    /// real-but-trivial IHostedService implementation; the
    /// hosted-service composition validator tests use real type
    /// descriptors (SlackOutboundDispatcher / SlackInboundIngestor)
    /// because the validator inspects the type identity, not the
    /// instance.
    /// </summary>
    private sealed class FakeDispatcherHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeIngestorHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Stand-in for a "real" production orchestrator client used by
    /// <see cref="AddSlackMessenger_accepts_real_orchestrator_pre_registered"/>
    /// to prove the facade's iter-3 guard satisfies the
    /// pre-registration path without forcing the dev-defaults
    /// opt-in. Behaviour is intentionally a no-op; only its CLR
    /// type identity matters (it MUST NOT be
    /// <c>NoOpAgentTaskService</c>) -- the test asserts the
    /// decorator wraps it.
    /// </summary>
    private sealed class RealOrchestratorStub : IAgentTaskService
    {
        public Task<AgentTaskCreationResult> CreateTaskAsync(AgentTaskCreationRequest request, CancellationToken ct)
            => Task.FromResult(new AgentTaskCreationResult("task-real-stub", request.CorrelationId, "created"));

        public Task<AgentTaskStatusResult> GetTaskStatusAsync(AgentTaskStatusQuery query, CancellationToken ct)
            => Task.FromResult(new AgentTaskStatusResult("real-stub", "stub", Array.Empty<AgentTaskStatusEntry>()));

        public Task PublishDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
            => Task.CompletedTask;
    }
}
