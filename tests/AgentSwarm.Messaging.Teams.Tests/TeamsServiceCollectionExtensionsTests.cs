using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.TeamsMessengerConnectorTests;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Verifies the DI wiring helpers from
/// <see cref="TeamsServiceCollectionExtensions"/> satisfy the brief's "wire
/// <c>TeamsMessengerConnector</c> into DI as <c>IMessengerConnector</c> keyed by
/// <c>&quot;teams&quot;</c>" requirement and that the in-process inbound channel exposes the
/// same <see cref="ChannelInboundEventPublisher"/> instance under both interfaces.
/// </summary>
public sealed class TeamsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddInProcessInboundEventChannel_BindsPublisherAndReader_ToSameInstance()
    {
        var services = new ServiceCollection();
        services.AddInProcessInboundEventChannel();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var publisher = sp.GetRequiredService<IInboundEventPublisher>();
        var reader = sp.GetRequiredService<IInboundEventReader>();
        var concrete = sp.GetRequiredService<ChannelInboundEventPublisher>();

        Assert.Same(concrete, publisher);
        Assert.Same(concrete, reader);
    }

    [Fact]
    public void AddTeamsMessengerConnector_RegistersConnectorAsKeyedMessengerConnector()
    {
        var services = BuildServices();
        services.AddTeamsMessengerConnector();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var keyed = sp.GetRequiredKeyedService<IMessengerConnector>(TeamsServiceCollectionExtensions.MessengerKey);
        var concrete = sp.GetRequiredService<TeamsMessengerConnector>();

        Assert.Same(concrete, keyed);
    }

    [Fact]
    public void AddTeamsMessengerConnector_AlsoWiresInProcessChannel()
    {
        var services = BuildServices();
        services.AddTeamsMessengerConnector();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        Assert.NotNull(sp.GetRequiredService<IInboundEventPublisher>());
        Assert.NotNull(sp.GetRequiredService<IInboundEventReader>());
    }

    [Fact]
    public void AddInProcessInboundEventChannel_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TeamsServiceCollectionExtensions.AddInProcessInboundEventChannel(null!));
    }

    [Fact]
    public void AddTeamsMessengerConnector_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TeamsServiceCollectionExtensions.AddTeamsMessengerConnector(null!));
    }

    /// <summary>
    /// Regression for evaluator-iter-1 finding #6 — the documentation claimed both helpers
    /// were idempotent, but the original implementation appended new singleton descriptors
    /// on every call. Now every registration uses <c>TryAdd*</c>, so calling the helpers
    /// any number of times leaves the descriptor count at exactly one per affected service
    /// type.
    /// </summary>
    [Fact]
    public void AddInProcessInboundEventChannel_CalledTwice_LeavesSingletonDescriptorsAtOnePerServiceType()
    {
        var services = new ServiceCollection();
        services.AddInProcessInboundEventChannel();
        services.AddInProcessInboundEventChannel();

        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(ChannelInboundEventPublisher)));
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IInboundEventPublisher)));
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IInboundEventReader)));
    }

    [Fact]
    public void AddTeamsMessengerConnector_CalledTwice_LeavesSingletonDescriptorsAtOnePerServiceType()
    {
        var services = BuildServices();
        services.AddTeamsMessengerConnector();
        services.AddTeamsMessengerConnector();

        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(TeamsMessengerConnector)));
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IMessengerConnector)
            && d.IsKeyedService
            && Equals(d.ServiceKey, TeamsServiceCollectionExtensions.MessengerKey)));
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(ChannelInboundEventPublisher)));
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IInboundEventPublisher)));
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IInboundEventReader)));
        // Stage 3.1 step 7 — the helper auto-registers IAdaptiveCardRenderer; idempotency
        // must hold for that descriptor too.
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IAdaptiveCardRenderer)));
    }

    /// <summary>
    /// Stage 3.1 step 7 — <see cref="TeamsServiceCollectionExtensions.AddTeamsMessengerConnector"/>
    /// must default-register an <see cref="IAdaptiveCardRenderer"/> implementation
    /// (canonical: <see cref="AdaptiveCardBuilder"/>) so the connector resolves end-to-end
    /// without forcing every host to write a renderer registration of its own. Hosts that
    /// ship a custom renderer can register it BEFORE calling the helper; the
    /// <c>TryAddSingleton</c> guard preserves the explicit registration.
    /// </summary>
    [Fact]
    public void AddTeamsMessengerConnector_RegistersAdaptiveCardRenderer_AsAdaptiveCardBuilder()
    {
        var services = BuildServices();
        services.AddTeamsMessengerConnector();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var renderer = sp.GetRequiredService<IAdaptiveCardRenderer>();
        Assert.IsType<AdaptiveCardBuilder>(renderer);

        var connector = sp.GetRequiredService<TeamsMessengerConnector>();
        Assert.NotNull(connector);
    }

    [Fact]
    public void AddTeamsMessengerConnector_ExplicitRendererPreRegistered_AutoWiringIsNoOp()
    {
        var explicitRenderer = new AdaptiveCardBuilder();
        var services = BuildServices();
        services.AddSingleton<IAdaptiveCardRenderer>(explicitRenderer);
        services.AddTeamsMessengerConnector();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var resolved = sp.GetRequiredService<IAdaptiveCardRenderer>();
        Assert.Same(explicitRenderer, resolved);
    }

    /// <summary>
    /// Regression for evaluator-iter-2 finding #1 — the connector ctor requires both
    /// <see cref="IConversationReferenceStore"/> and <see cref="IConversationReferenceRouter"/>,
    /// but only the store is part of the canonical Stage 2.1 contract. When the host
    /// registers a single store implementation that satisfies BOTH interfaces (the documented
    /// pattern for Stage 2.1's <c>InMemoryConversationReferenceStore</c> and Stage 4.1's
    /// <c>SqlConversationReferenceStore</c>), <see cref="TeamsServiceCollectionExtensions.AddTeamsMessengerConnector"/>
    /// must auto-wire the router so the keyed connector resolves end-to-end without forcing
    /// the host to write a router-only registration of its own. This test asserts the
    /// auto-wired router resolves to the SAME singleton instance as the store, so calls
    /// against either contract observe the same in-memory state.
    /// </summary>
    [Fact]
    public void AddTeamsMessengerConnector_StoreImplementsBothInterfaces_AutoWiresRouterToSameSingleton()
    {
        var services = BuildServicesWithoutSeparateRouter<DualInterfaceConversationReferenceStore>();
        services.AddTeamsMessengerConnector();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var store = sp.GetRequiredService<IConversationReferenceStore>();
        var router = sp.GetRequiredService<IConversationReferenceRouter>();
        var connector = sp.GetRequiredService<TeamsMessengerConnector>();
        var keyedConnector = sp.GetRequiredKeyedService<IMessengerConnector>(TeamsServiceCollectionExtensions.MessengerKey);

        Assert.Same(store, router);
        Assert.IsType<DualInterfaceConversationReferenceStore>(router);
        Assert.Same(connector, keyedConnector);
    }

    /// <summary>
    /// Regression for evaluator-iter-2 finding #1 (failure mode) — when the host registers
    /// a store that does NOT implement <see cref="IConversationReferenceRouter"/> AND does
    /// not register a separate router, the auto-wiring must fail loudly at first connector
    /// resolution with a descriptive message that names the offending store type, names
    /// both interfaces, and points the operator at the canonical fix (use a store that
    /// implements both interfaces, OR pre-register a router before calling the helper).
    /// </summary>
    [Fact]
    public void AddTeamsMessengerConnector_StoreDoesNotImplementRouter_ResolvingRouterThrowsWithDescriptiveMessage()
    {
        var services = BuildServicesWithoutSeparateRouter<StoreOnlyConversationReferenceStore>();
        services.AddTeamsMessengerConnector();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var ex = Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<IConversationReferenceRouter>());

        Assert.Contains(typeof(StoreOnlyConversationReferenceStore).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("IConversationReferenceRouter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddTeamsMessengerConnector", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Confirms the auto-wiring is a TRUE no-op when the host pre-registers an explicit
    /// <see cref="IConversationReferenceRouter"/>. The explicit registration wins and the
    /// connector receives that instance, NOT the cast adapter.
    /// </summary>
    [Fact]
    public void AddTeamsMessengerConnector_ExplicitRouterPreRegistered_AutoWiringIsNoOp()
    {
        var services = BuildServicesWithoutSeparateRouter<DualInterfaceConversationReferenceStore>();
        var explicitRouter = new RecordingConversationReferenceRouter();
        services.AddSingleton<IConversationReferenceRouter>(explicitRouter);
        services.AddTeamsMessengerConnector();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var resolvedRouter = sp.GetRequiredService<IConversationReferenceRouter>();
        Assert.Same(explicitRouter, resolvedRouter);
        Assert.IsType<RecordingConversationReferenceRouter>(resolvedRouter);
    }

    /// <summary>
    /// Stage 3.3 step 6 / iter-5 critique #1 — the lifecycle helper must register
    /// every Stage 3.3 collaborator in a single call: <see cref="ITeamsCardManager"/>
    /// (delegating to the same <see cref="TeamsMessengerConnector"/> singleton),
    /// <see cref="ICardActionHandler"/>, and the <see cref="QuestionExpiryProcessor"/>
    /// hosted service. This test pins the wiring shape so a regression that splits
    /// the helper or drops a registration trips the suite.
    /// </summary>
    [Fact]
    public void AddTeamsCardLifecycle_RegistersCardManager_Handler_AndExpiryHostedService()
    {
        var services = BuildServices();
        services.AddSingleton<IAuditLogger, RecordingAuditLogger>();

        services.AddTeamsCardLifecycle();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        // ITeamsCardManager resolves and is the same singleton as the connector.
        var cardManager = sp.GetRequiredService<ITeamsCardManager>();
        var connector = sp.GetRequiredService<TeamsMessengerConnector>();
        Assert.Same(connector, cardManager);

        // ICardActionHandler is the concrete CardActionHandler (replacing any NoOp stub).
        var handler = sp.GetRequiredService<ICardActionHandler>();
        Assert.IsType<CardActionHandler>(handler);

        // QuestionExpiryProcessor is registered as IHostedService.
        var hosted = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
        Assert.Contains(hosted, h => h is AgentSwarm.Messaging.Teams.Lifecycle.QuestionExpiryProcessor);
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<CloudAdapter>(_ => new RecordingCloudAdapter());
        services.AddSingleton(new TeamsMessagingOptions { MicrosoftAppId = "app-id" });
        services.AddSingleton<IConversationReferenceStore, ConnectorRecordingConversationReferenceStore>();
        services.AddSingleton<IConversationReferenceRouter, RecordingConversationReferenceRouter>();
        services.AddSingleton<IAgentQuestionStore, RecordingAgentQuestionStore>();
        services.AddSingleton<ICardStateStore, RecordingCardStateStore>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        return services;
    }

    /// <summary>
    /// Variant of <see cref="BuildServices"/> that intentionally does NOT register a
    /// separate <see cref="IConversationReferenceRouter"/>, so the cast-adapter logic in
    /// <see cref="TeamsServiceCollectionExtensions.AddTeamsMessengerConnector"/> is the
    /// code path under test. The store concrete type is parameterized so the same harness
    /// can drive both the success path (store implements both interfaces) and the failure
    /// path (store implements only <see cref="IConversationReferenceStore"/>).
    /// </summary>
    private static ServiceCollection BuildServicesWithoutSeparateRouter<TStore>()
        where TStore : class, IConversationReferenceStore
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<CloudAdapter>(_ => new RecordingCloudAdapter());
        services.AddSingleton(new TeamsMessagingOptions { MicrosoftAppId = "app-id" });
        services.AddSingleton<IConversationReferenceStore, TStore>();
        services.AddSingleton<IAgentQuestionStore, RecordingAgentQuestionStore>();
        services.AddSingleton<ICardStateStore, RecordingCardStateStore>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        return services;
    }

    /// <summary>
    /// Test double mimicking the canonical store implementations (Stage 2.1
    /// <c>InMemoryConversationReferenceStore</c>, Stage 4.1
    /// <c>SqlConversationReferenceStore</c>) that satisfy BOTH the store and router
    /// contracts in a single class. Used to exercise the cast-adapter success path.
    /// </summary>
    public sealed class DualInterfaceConversationReferenceStore
        : IConversationReferenceStore, IConversationReferenceRouter
    {
        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct) => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());
        public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());
        public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.FromResult(false);
        public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;

        public Task<TeamsConversationReference?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<TeamsConversationReference?>(null);
    }

    /// <summary>
    /// Test double mimicking a host-supplied store that does NOT implement the companion
    /// router contract — used to exercise the cast-adapter failure path. Implements only
    /// <see cref="IConversationReferenceStore"/> so the cast in
    /// <see cref="TeamsServiceCollectionExtensions.AddTeamsMessengerConnector"/> returns
    /// null and the descriptive throw fires.
    /// </summary>
    public sealed class StoreOnlyConversationReferenceStore : IConversationReferenceStore
    {
        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct) => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());
        public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());
        public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.FromResult(false);
        public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
    }
}
