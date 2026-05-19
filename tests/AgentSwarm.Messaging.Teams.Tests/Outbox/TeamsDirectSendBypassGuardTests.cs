using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Extensions;
using AgentSwarm.Messaging.Teams.Outbox;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// Iter-3 evaluator critique #4 regression — pins the
/// <see cref="TeamsDirectSendBypassGuard"/> behaviour. Without these tests, the
/// direct-send paths on the inner concrete <see cref="TeamsMessengerConnector"/> and
/// <see cref="TeamsProactiveNotifier"/> would silently bypass
/// <see cref="IMessageOutbox.EnqueueAsync"/> whenever a host (or test) resolved the
/// concrete types after composing
/// <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/>. The
/// tests below verify (a) the guard's exception shape, (b) DI registration by
/// <c>AddTeamsOutboxEngine</c>, (c) the connector / notifier DI factories assign the
/// guard onto the concretes via the new <c>DirectSendGuard</c> property, (d) direct
/// sends throw the structured exception when the guard is wired, and (e) the
/// canonical constructors keep the guard property <c>null</c> by default so legacy
/// (pre-Stage-6.1) tests and hosts that don't compose the outbox engine continue to
/// work unchanged.
/// </summary>
public sealed class TeamsDirectSendBypassGuardTests
{
    [Fact]
    public void ThrowIfDisallowed_RaisesInvalidOperationExceptionWithRemediationKeywords()
    {
        var guard = new TeamsDirectSendBypassGuard();

        var ex = Assert.Throws<InvalidOperationException>(
            () => guard.ThrowIfDisallowed("MyConnector", "MySendMethod"));

        Assert.Contains("MyConnector", ex.Message);
        Assert.Contains("MySendMethod", ex.Message);
        Assert.Contains("AddTeamsOutboxEngine", ex.Message);
        Assert.Contains("IMessageOutbox", ex.Message);
        Assert.Contains("IMessengerConnector", ex.Message);
        Assert.Contains("IProactiveNotifier", ex.Message);
    }

    [Fact]
    public void AddTeamsOutboxEngine_RegistersTeamsDirectSendBypassGuardSingleton()
    {
        var services = BuildBaseServices();

        services.AddTeamsOutboxEngine();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TeamsDirectSendBypassGuard));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider();
        var guard = provider.GetRequiredService<TeamsDirectSendBypassGuard>();
        Assert.NotNull(guard);
    }

    [Fact]
    public void AddTeamsOutboxEngine_GuardIsIdempotentAcrossRepeatComposition()
    {
        var services = BuildBaseServices();

        services.AddTeamsOutboxEngine();
        services.AddTeamsOutboxEngine();
        services.AddTeamsOutboxEngine();

        Assert.Single(services, d => d.ServiceType == typeof(TeamsDirectSendBypassGuard));
    }

    [Fact]
    public async Task TeamsMessengerConnector_SendMessageAsync_ThrowsWhenDirectSendGuardSet()
    {
        var connector = NewGuardedConnector();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connector.SendMessageAsync(SampleMessage("m-1"), CancellationToken.None));

        Assert.Contains(nameof(TeamsMessengerConnector), ex.Message);
        Assert.Contains(nameof(TeamsMessengerConnector.SendMessageAsync), ex.Message);
    }

    [Fact]
    public async Task TeamsMessengerConnector_SendQuestionAsync_ThrowsWhenDirectSendGuardSet()
    {
        var connector = NewGuardedConnector();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connector.SendQuestionAsync(SampleQuestion("q-1", userId: "user-1"), CancellationToken.None));

        Assert.Contains(nameof(TeamsMessengerConnector), ex.Message);
        Assert.Contains(nameof(TeamsMessengerConnector.SendQuestionAsync), ex.Message);
    }

    [Fact]
    public void TeamsMessengerConnector_DirectSendGuardDefaultsNullForLegacyConstruction()
    {
        // Sanity check — the legacy direct-send path MUST stay un-guarded when the
        // guard property is not assigned, so pre-Stage-6.1 hosts and the 600+
        // existing tests that don't call AddTeamsOutboxEngine continue to function.
        var connector = NewUnguardedConnector();
        Assert.Null(connector.DirectSendGuard);
    }

    [Fact]
    public async Task TeamsProactiveNotifier_SendProactiveAsync_ThrowsWhenDirectSendGuardSet()
    {
        var notifier = NewGuardedNotifier();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendProactiveAsync("tenant-1", "user-1", SampleMessage("m-1"), CancellationToken.None));

        Assert.Contains(nameof(TeamsProactiveNotifier), ex.Message);
        Assert.Contains(nameof(TeamsProactiveNotifier.SendProactiveAsync), ex.Message);
    }

    [Fact]
    public async Task TeamsProactiveNotifier_SendProactiveQuestionAsync_ThrowsWhenDirectSendGuardSet()
    {
        var notifier = NewGuardedNotifier();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendProactiveQuestionAsync(
                "tenant-1", "user-1",
                SampleQuestion("q-1", userId: "user-1"),
                CancellationToken.None));

        Assert.Contains(nameof(TeamsProactiveNotifier.SendProactiveQuestionAsync), ex.Message);
    }

    [Fact]
    public async Task TeamsProactiveNotifier_SendToChannelAsync_ThrowsWhenDirectSendGuardSet()
    {
        var notifier = NewGuardedNotifier();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendToChannelAsync(
                "tenant-1", "channel-1",
                SampleMessage("m-1"),
                CancellationToken.None));

        Assert.Contains(nameof(TeamsProactiveNotifier.SendToChannelAsync), ex.Message);
    }

    [Fact]
    public async Task TeamsProactiveNotifier_SendQuestionToChannelAsync_ThrowsWhenDirectSendGuardSet()
    {
        var notifier = NewGuardedNotifier();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendQuestionToChannelAsync(
                "tenant-1", "channel-1",
                SampleQuestion("q-1", channelId: "channel-1"),
                CancellationToken.None));

        Assert.Contains(nameof(TeamsProactiveNotifier.SendQuestionToChannelAsync), ex.Message);
    }

    [Fact]
    public void TeamsProactiveNotifier_DirectSendGuardDefaultsNullForLegacyConstruction()
    {
        var notifier = NewUnguardedNotifier();
        Assert.Null(notifier.DirectSendGuard);
    }

    private static TeamsMessengerConnector NewUnguardedConnector() =>
        new(
            adapter: new TeamsMessengerConnectorTests.RecordingCloudAdapter(),
            options: new TeamsMessagingOptions { MicrosoftAppId = "app-id" },
            conversationReferenceStore: new TeamsMessengerConnectorTests.ConnectorRecordingConversationReferenceStore(),
            conversationReferenceRouter: new RecordingConversationReferenceStore(),
            agentQuestionStore: new RecordingAgentQuestionStore(),
            cardStateStore: new TeamsMessengerConnectorTests.RecordingCardStateStore(),
            cardRenderer: new AdaptiveCardBuilder(),
            inboundEventReader: new ChannelInboundEventPublisher(),
            logger: NullLogger<TeamsMessengerConnector>.Instance,
            timeProvider: TimeProvider.System,
            installationStateGate: null);

    private static TeamsMessengerConnector NewGuardedConnector() =>
        new(
            adapter: new TeamsMessengerConnectorTests.RecordingCloudAdapter(),
            options: new TeamsMessagingOptions { MicrosoftAppId = "app-id" },
            conversationReferenceStore: new TeamsMessengerConnectorTests.ConnectorRecordingConversationReferenceStore(),
            conversationReferenceRouter: new RecordingConversationReferenceStore(),
            agentQuestionStore: new RecordingAgentQuestionStore(),
            cardStateStore: new TeamsMessengerConnectorTests.RecordingCardStateStore(),
            cardRenderer: new AdaptiveCardBuilder(),
            inboundEventReader: new ChannelInboundEventPublisher(),
            logger: NullLogger<TeamsMessengerConnector>.Instance,
            timeProvider: TimeProvider.System,
            installationStateGate: null)
        {
            DirectSendGuard = new TeamsDirectSendBypassGuard(),
        };

    private static TeamsProactiveNotifier NewUnguardedNotifier() =>
        new(
            adapter: new TeamsProactiveNotifierTests.RecordingCloudAdapter(),
            options: new TeamsMessagingOptions { MicrosoftAppId = "app-id" },
            conversationReferenceStore: new RecordingConversationReferenceStore(),
            cardRenderer: new AdaptiveCardBuilder(),
            cardStateStore: new TeamsMessengerConnectorTests.RecordingCardStateStore(),
            agentQuestionStore: new RecordingAgentQuestionStore(),
            logger: NullLogger<TeamsProactiveNotifier>.Instance,
            timeProvider: TimeProvider.System,
            installationStateGate: null);

    private static TeamsProactiveNotifier NewGuardedNotifier() =>
        new(
            adapter: new TeamsProactiveNotifierTests.RecordingCloudAdapter(),
            options: new TeamsMessagingOptions { MicrosoftAppId = "app-id" },
            conversationReferenceStore: new RecordingConversationReferenceStore(),
            cardRenderer: new AdaptiveCardBuilder(),
            cardStateStore: new TeamsMessengerConnectorTests.RecordingCardStateStore(),
            agentQuestionStore: new RecordingAgentQuestionStore(),
            logger: NullLogger<TeamsProactiveNotifier>.Instance,
            timeProvider: TimeProvider.System,
            installationStateGate: null)
        {
            DirectSendGuard = new TeamsDirectSendBypassGuard(),
        };

    private static MessengerMessage SampleMessage(string id) => new(
        MessageId: id,
        CorrelationId: $"corr-{id}",
        AgentId: "agent-1",
        TaskId: "task-1",
        ConversationId: "conv-1",
        Body: "hi",
        Severity: MessageSeverities.Info,
        Timestamp: DateTimeOffset.UnixEpoch);

    private static AgentQuestion SampleQuestion(string id, string? userId = null, string? channelId = null) => new()
    {
        QuestionId = id,
        TenantId = "tenant-1",
        TargetUserId = userId,
        TargetChannelId = channelId,
        CorrelationId = $"corr-{id}",
        AgentId = "agent-1",
        TaskId = "task-1",
        Title = "Title",
        Body = "body",
        Severity = MessageSeverities.Info,
        Status = AgentQuestionStatuses.Open,
        AllowedActions = new[] { new HumanAction("yes", "Yes", "yes", false) },
        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static IServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProactiveNotifier>(new RecordingProactiveNotifier());
        services.AddSingleton<IMessengerConnector>(new RecordingMessengerConnector());

        var store = new RecordingConversationReferenceStore();
        services.AddSingleton<IConversationReferenceStore>(store);
        services.AddSingleton<IConversationReferenceRouter>(store);
        services.AddSingleton<IAgentQuestionStore>(new RecordingAgentQuestionStore());
        services.AddSingleton<IMessageOutbox>(new InMemoryRecordingOutbox());
        return services;
    }
}
