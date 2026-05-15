using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Behavioral tests for the Stage 2.1 no-op DI stubs that hold real state or have non-trivial
/// reply paths: <see cref="NoOpCardStateStore"/> (concurrent dictionary), <see cref="NoOpCommandDispatcher"/>
/// (turn-context-aware reply), and <see cref="NoOpCardActionHandler"/>
/// (returns the canonical "not yet available" Adaptive Card invoke response).
/// </summary>
/// <remarks>
/// These stubs are wired into <c>Program.cs</c> as the default implementations until Stages
/// 3.2 / 3.3 land. Tests pin behavior so a regression in the stubs (which power local dev and
/// integration tests for downstream stages) breaks loudly rather than silently misrouting.
/// </remarks>
public sealed class NoOpStubsTests
{
    [Fact]
    public async Task NoOpCardStateStore_RoundTrips_SaveGetUpdate()
    {
        var store = new NoOpCardStateStore();
        var state = new TeamsCardState
        {
            QuestionId = "q-1",
            ActivityId = "act-1",
            ConversationId = "conv-1",
            ConversationReferenceJson = "{}",
            Status = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(state, CancellationToken.None);
        var fetched = await store.GetByQuestionIdAsync("q-1", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal("act-1", fetched!.ActivityId);
        Assert.Equal("Pending", fetched.Status);

        await store.UpdateStatusAsync("q-1", "Answered", CancellationToken.None);
        var afterUpdate = await store.GetByQuestionIdAsync("q-1", CancellationToken.None);

        Assert.Equal("Answered", afterUpdate!.Status);
        Assert.True(afterUpdate.UpdatedAt >= state.UpdatedAt);
    }

    [Fact]
    public async Task NoOpCardStateStore_GetByUnknownQuestionId_ReturnsNull()
    {
        var store = new NoOpCardStateStore();

        var fetched = await store.GetByQuestionIdAsync("unknown", CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task NoOpCardStateStore_RejectsNullState()
    {
        var store = new NoOpCardStateStore();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.SaveAsync(state: null!, CancellationToken.None));
    }

    [Fact]
    public async Task NoOpCardActionHandler_Returns200_WithMessageBody()
    {
        // The activity handler invokes ICardActionHandler.HandleAsync to obtain the
        // AdaptiveCardInvokeResponse to send back to Teams. The Stage 2.1 stub must return a
        // valid 200-status response so the activity handler does not surface an error to the
        // user; the actual decision-recording logic ships in Stage 3.3.
        var handler = new NoOpCardActionHandler();
        var turnContext = new FakeTurnContext();

        var response = await handler.HandleAsync(turnContext, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.Type);
        Assert.NotNull(response.Value);
    }

    [Fact]
    public async Task NoOpCardActionHandler_RejectsNullTurnContext()
    {
        var handler = new NoOpCardActionHandler();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => handler.HandleAsync(turnContext: null!, CancellationToken.None));
    }

    [Fact]
    public async Task NoOpCommandDispatcher_RepliesViaTurnContext_WhenAvailable()
    {
        // The Stage 2.1 stub keeps the bot conversational while the production CommandDispatcher
        // ships in Stage 3.2: when a turn context is available it posts a "commands not yet
        // available" reply through the active conversation. We assert the reply travels
        // through ITurnContext.SendActivityAsync exactly once.
        var dispatcher = new NoOpCommandDispatcher(NullLogger<NoOpCommandDispatcher>.Instance);
        var turnContext = new FakeTurnContext();
        var ctx = new CommandContext
        {
            NormalizedText = "agent ask demo",
            CorrelationId = "corr-test",
            TurnContext = turnContext,
        };

        await dispatcher.DispatchAsync(ctx, CancellationToken.None);

        Assert.Single(turnContext.SentActivities);
        var sent = turnContext.SentActivities[0];
        Assert.Equal(ActivityTypes.Message, sent.Type);
        var sentText = (sent as IMessageActivity)?.Text ?? string.Empty;
        Assert.Contains(
            "not yet available",
            sentText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoOpCommandDispatcher_NoTurnContext_NoSendActivities()
    {
        // Without a turn context (e.g., during background unit-test invocations) the
        // dispatcher must not throw and must not attempt to send any activity.
        var dispatcher = new NoOpCommandDispatcher(NullLogger<NoOpCommandDispatcher>.Instance);
        var ctx = new CommandContext
        {
            NormalizedText = "agent status",
            CorrelationId = "corr-test",
            TurnContext = null,
        };

        await dispatcher.DispatchAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task NoOpCommandDispatcher_RejectsNullContext()
    {
        var dispatcher = new NoOpCommandDispatcher(NullLogger<NoOpCommandDispatcher>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => dispatcher.DispatchAsync(context: null!, CancellationToken.None));
    }

    [Fact]
    public async Task NoOpCardManager_LogsAndCompletes_WithoutThrow()
    {
        // The Stage 2.1 stub for ITeamsCardManager logs and returns; verifying it completes
        // without throwing keeps the wiring honest until the production TeamsMessengerConnector
        // implementation lands in Stage 3.3.
        var manager = new NoOpCardManager(NullLogger<NoOpCardManager>.Instance);

        await manager.UpdateCardAsync("q-1", CardUpdateAction.MarkAnswered, CancellationToken.None);
        await manager.DeleteCardAsync("q-1", CancellationToken.None);
    }

    /// <summary>
    /// Minimal <see cref="ITurnContext"/> double for stub testing — captures sent activities
    /// for assertion. Avoids pulling in a Bot Framework adapter mock.
    /// </summary>
    private sealed class FakeTurnContext : ITurnContext
    {
        public List<IActivity> SentActivities { get; } = new();

        public BotAdapter Adapter => throw new NotSupportedException();
        public TurnContextStateCollection TurnState { get; } = new();
        public Activity Activity { get; } = new()
        {
            Type = ActivityTypes.Message,
            Id = "fake-act-1",
            Text = "hello",
            Conversation = new ConversationAccount { Id = "fake-conv-1" },
            From = new ChannelAccount { Id = "fake-user-1", Name = "User" },
            Recipient = new ChannelAccount { Id = "fake-bot-1", Name = "Bot" },
            ServiceUrl = "https://smba.example/",
        };

        public bool Responded => SentActivities.Count > 0;

        public Task DeleteActivityAsync(string activityId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteActivityAsync(ConversationReference conversationReference, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ITurnContext OnDeleteActivity(DeleteActivityHandler handler) => this;
        public ITurnContext OnSendActivities(SendActivitiesHandler handler) => this;
        public ITurnContext OnUpdateActivity(UpdateActivityHandler handler) => this;

        public Task<ResourceResponse[]> SendActivitiesAsync(IActivity[] activities, CancellationToken cancellationToken = default)
        {
            SentActivities.AddRange(activities);
            return Task.FromResult(activities
                .Select(_ => new ResourceResponse(Guid.NewGuid().ToString()))
                .ToArray());
        }

        public Task<ResourceResponse> SendActivityAsync(string textReplyToSend, string? speak = null, string inputHint = "acceptingInput", CancellationToken cancellationToken = default)
        {
            var act = MessageFactory.Text(textReplyToSend);
            SentActivities.Add(act);
            return Task.FromResult(new ResourceResponse(Guid.NewGuid().ToString()));
        }

        public Task<ResourceResponse> SendActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
        {
            SentActivities.Add(activity);
            return Task.FromResult(new ResourceResponse(Guid.NewGuid().ToString()));
        }

        public Task<ResourceResponse> UpdateActivityAsync(IActivity activity, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceResponse(activity.Id ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)));
    }
}
