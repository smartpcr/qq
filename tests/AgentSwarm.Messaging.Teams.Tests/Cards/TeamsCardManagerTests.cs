using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Rest;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using static AgentSwarm.Messaging.Teams.Tests.TeamsMessengerConnectorTests;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Cards;

/// <summary>
/// Covers the <see cref="ITeamsCardManager"/> behaviours implemented by
/// <see cref="TeamsMessengerConnector"/> per Stage 3.3 of <c>implementation-plan.md</c>:
/// update / delete card path, terminal status mapping
/// (<c>MarkCancelled → Expired</c>, <c>DeleteCardAsync → Expired</c>), inline retry on
/// transient Bot Framework failures, stale-activity 404 fallback, and missing-card-state
/// failure mode.
/// </summary>
public sealed class TeamsCardManagerTests
{
    private const string AppId = "11111111-1111-1111-1111-111111111111";
    private const string QuestionId = "q-card-001";
    private const string ActivityId = "act-001";
    private const string ConversationId = "19:conversation-z";

    private static TeamsCardState BuildCardState(string status = TeamsCardStatuses.Pending)
    {
        var reference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.example.test",
            Conversation = new ConversationAccount(id: ConversationId, tenantId: "tenant-a"),
            Bot = new ChannelAccount(id: $"28:{AppId}"),
            User = new ChannelAccount(id: "29:user-dave", aadObjectId: "aad-dave", name: "Dave Test"),
        };

        return new TeamsCardState
        {
            QuestionId = QuestionId,
            ActivityId = ActivityId,
            ConversationId = ConversationId,
            ConversationReferenceJson = JsonConvert.SerializeObject(reference),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
    }

    private sealed class CardManagerHarness
    {
        public required TeamsMessengerConnector Connector { get; init; }
        public required RecordingCloudAdapter Adapter { get; init; }
        public required RecordingCardStateStore CardStateStore { get; init; }
        public required TeamsMessagingOptions Options { get; init; }

        public static CardManagerHarness Build(RecordingCloudAdapter? adapter = null)
        {
            adapter ??= new RecordingCloudAdapter();
            var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };
            var convStore = new ConnectorRecordingConversationReferenceStore();
            var router = new RecordingConversationReferenceRouter();
            var qStore = new RecordingAgentQuestionStore();
            var cardStore = new RecordingCardStateStore();
            var renderer = new AdaptiveCardBuilder();
            IInboundEventReader reader = new ChannelInboundEventPublisher();
            var connector = new TeamsMessengerConnector(
                adapter,
                options,
                convStore,
                router,
                qStore,
                cardStore,
                renderer,
                reader,
                NullLogger<TeamsMessengerConnector>.Instance);
            return new CardManagerHarness
            {
                Connector = connector,
                Adapter = adapter,
                CardStateStore = cardStore,
                Options = options,
            };
        }
    }

    [Fact]
    public async Task UpdateCardAsync_MarkAnswered_PersistsAnsweredStatus()
    {
        var harness = CardManagerHarness.Build();
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        ITeamsCardManager cardManager = harness.Connector;

        await cardManager.UpdateCardAsync(QuestionId, CardUpdateAction.MarkAnswered, CancellationToken.None);

        Assert.Single(harness.Adapter.ContinueCalls);
        var update = Assert.Single(harness.CardStateStore.StatusUpdates);
        Assert.Equal(QuestionId, update.QuestionId);
        Assert.Equal(TeamsCardStatuses.Answered, update.Status);
    }

    [Fact]
    public async Task UpdateCardAsync_MarkCancelled_PersistsExpiredStatus()
    {
        // Iter-3 critique #3 fix: MarkCancelled must NOT fall through to Answered.
        var harness = CardManagerHarness.Build();
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        ITeamsCardManager cardManager = harness.Connector;

        await cardManager.UpdateCardAsync(QuestionId, CardUpdateAction.MarkCancelled, CancellationToken.None);

        var update = Assert.Single(harness.CardStateStore.StatusUpdates);
        Assert.Equal(TeamsCardStatuses.Expired, update.Status);
    }

    [Fact]
    public async Task UpdateCardAsync_MarkExpired_PersistsExpiredStatus()
    {
        var harness = CardManagerHarness.Build();
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        ITeamsCardManager cardManager = harness.Connector;

        await cardManager.UpdateCardAsync(QuestionId, CardUpdateAction.MarkExpired, CancellationToken.None);

        var update = Assert.Single(harness.CardStateStore.StatusUpdates);
        Assert.Equal(TeamsCardStatuses.Expired, update.Status);
    }

    [Fact]
    public async Task UpdateCardAsync_WithActorDecision_PersistsAnsweredStatus()
    {
        // Iter-2 critique #1 fix: the actor-attributed overload propagates the decision
        // and actor display name into the replacement card.
        var harness = CardManagerHarness.Build();
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        ITeamsCardManager cardManager = harness.Connector;

        var decision = new HumanDecisionEvent(
            QuestionId: QuestionId,
            ActionValue: "approve",
            Comment: null,
            Messenger: "Teams",
            ExternalUserId: "aad-user-1",
            ExternalMessageId: "msg-1",
            ReceivedAt: DateTimeOffset.UtcNow,
            CorrelationId: "corr-z");

        await cardManager.UpdateCardAsync(
            QuestionId,
            CardUpdateAction.MarkAnswered,
            decision,
            actorDisplayName: "Alice Wong",
            CancellationToken.None);

        Assert.Equal(TeamsCardStatuses.Answered, harness.CardStateStore.StatusUpdates.Single().Status);
    }

    [Fact]
    public async Task DeleteCardAsync_PersistsCardStateAsExpired_NotDeleted()
    {
        // Iter-5 critique #2 fix: DeleteCardAsync must persist card-state status as
        // Expired (NOT a new "Deleted" status). The canonical vocabulary is
        // Pending/Answered/Expired only.
        var harness = CardManagerHarness.Build();
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        ITeamsCardManager cardManager = harness.Connector;

        await cardManager.DeleteCardAsync(QuestionId, CancellationToken.None);

        var update = Assert.Single(harness.CardStateStore.StatusUpdates);
        Assert.Equal(TeamsCardStatuses.Expired, update.Status);
        Assert.Single(harness.Adapter.ContinueCalls);
    }

    [Fact]
    public async Task UpdateCardAsync_MissingCardState_ThrowsInvalidOperationException()
    {
        var harness = CardManagerHarness.Build();
        ITeamsCardManager cardManager = harness.Connector;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cardManager.UpdateCardAsync("nonexistent", CardUpdateAction.MarkAnswered, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteCardAsync_MissingCardState_ThrowsInvalidOperationException()
    {
        var harness = CardManagerHarness.Build();
        ITeamsCardManager cardManager = harness.Connector;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cardManager.DeleteCardAsync("nonexistent", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateCardAsync_TransientBotConnectorFailure_RetriesAndSucceeds()
    {
        // Iter-2 critique #4: retry must classify Bot Connector transient failures
        // properly. Use an ErrorResponseException with a 503 status (canonical
        // transient response) rather than HttpRequestException("Simulated transient 503").
        var adapter = new TransientThenSuccessUpdateAdapter(failuresBeforeSuccess: 2);
        var harness = CardManagerHarness.Build(adapter);
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        harness.Options.MaxRetryAttempts = 5;
        harness.Options.RetryBaseDelaySeconds = 1;
        ITeamsCardManager cardManager = harness.Connector;

        await cardManager.UpdateCardAsync(QuestionId, CardUpdateAction.MarkAnswered, CancellationToken.None);

        Assert.Equal(3, adapter.UpdateAttempts);
        Assert.Equal(TeamsCardStatuses.Answered, harness.CardStateStore.StatusUpdates.Single().Status);
    }

    [Fact]
    public async Task UpdateCardAsync_StaleActivity404_SendsReplacementAndUpdatesActivityId()
    {
        var adapter = new Stale404UpdateAdapter();
        var harness = CardManagerHarness.Build(adapter);
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        ITeamsCardManager cardManager = harness.Connector;

        await cardManager.UpdateCardAsync(QuestionId, CardUpdateAction.MarkAnswered, CancellationToken.None);

        // The stale-404 fallback should have triggered a send (replacement card).
        Assert.NotEmpty(adapter.Sent);
        // The save path should have re-persisted card state with the new activity ID
        // and the Answered status (NOT just an UpdateStatusAsync row).
        var resave = Assert.Single(harness.CardStateStore.Saved);
        Assert.Equal(TeamsCardStatuses.Answered, resave.Status);
        Assert.NotEqual(ActivityId, resave.ActivityId);
    }

    [Fact]
    public async Task DeleteCardAsync_Stale404_TreatsAsSuccess()
    {
        var adapter = new Stale404DeleteAdapter();
        var harness = CardManagerHarness.Build(adapter);
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        ITeamsCardManager cardManager = harness.Connector;

        // Should NOT throw — 404 on delete is "already gone" per e2e-scenarios.md.
        await cardManager.DeleteCardAsync(QuestionId, CancellationToken.None);

        // Status should still be persisted as Expired.
        Assert.Equal(TeamsCardStatuses.Expired, harness.CardStateStore.StatusUpdates.Single().Status);
    }

    [Fact]
    public async Task UpdateCardAsync_TransientFailureExhaustsRetries_Throws()
    {
        var adapter = new TransientThenSuccessUpdateAdapter(failuresBeforeSuccess: 99);
        var harness = CardManagerHarness.Build(adapter);
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        harness.Options.MaxRetryAttempts = 3;
        harness.Options.RetryBaseDelaySeconds = 1;
        ITeamsCardManager cardManager = harness.Connector;

        var ex = await Assert.ThrowsAsync<ErrorResponseException>(() =>
            cardManager.UpdateCardAsync(QuestionId, CardUpdateAction.MarkAnswered, CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.Response?.StatusCode);
        Assert.Equal(3, adapter.UpdateAttempts);
    }

    [Fact]
    public async Task UpdateCardAsync_NonTransientFailure_NoRetry_Throws()
    {
        var adapter = new NonTransientUpdateAdapter();
        var harness = CardManagerHarness.Build(adapter);
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        harness.Options.MaxRetryAttempts = 5;
        harness.Options.RetryBaseDelaySeconds = 1;
        ITeamsCardManager cardManager = harness.Connector;

        await Assert.ThrowsAsync<ErrorResponseException>(() =>
            cardManager.UpdateCardAsync(QuestionId, CardUpdateAction.MarkAnswered, CancellationToken.None));

        // No retries on non-transient (400).
        Assert.Equal(1, adapter.UpdateAttempts);
    }

    [Fact]
    public async Task UpdateCardAsync_WithDecisionOverload_NullDecision_Throws()
    {
        var harness = CardManagerHarness.Build();
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();
        ITeamsCardManager cardManager = harness.Connector;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            cardManager.UpdateCardAsync(QuestionId, CardUpdateAction.MarkAnswered, decision: null!, actorDisplayName: null, CancellationToken.None));
    }

    private static ErrorResponseException BuildErrorResponseException(HttpStatusCode statusCode)
    {
        var ex = new ErrorResponseException($"HTTP {(int)statusCode}");
        ex.Response = new HttpResponseMessageWrapper(
            new HttpResponseMessage(statusCode),
            string.Empty);
        return ex;
    }

    private sealed class TransientThenSuccessUpdateAdapter : RecordingCloudAdapter
    {
        private readonly int _failuresBeforeSuccess;

        public TransientThenSuccessUpdateAdapter(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int UpdateAttempts { get; private set; }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
        {
            UpdateAttempts++;
            if (UpdateAttempts <= _failuresBeforeSuccess)
            {
                throw BuildErrorResponseException(HttpStatusCode.ServiceUnavailable);
            }

            return Task.FromResult(new ResourceResponse(activity.Id ?? Guid.NewGuid().ToString()));
        }
    }

    private sealed class Stale404UpdateAdapter : RecordingCloudAdapter
    {
        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
        {
            throw BuildErrorResponseException(HttpStatusCode.NotFound);
        }
    }

    private sealed class Stale404DeleteAdapter : RecordingCloudAdapter
    {
        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
        {
            throw BuildErrorResponseException(HttpStatusCode.NotFound);
        }
    }

    private sealed class NonTransientUpdateAdapter : RecordingCloudAdapter
    {
        public int UpdateAttempts { get; private set; }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
        {
            UpdateAttempts++;
            throw BuildErrorResponseException(HttpStatusCode.BadRequest);
        }
    }
}
