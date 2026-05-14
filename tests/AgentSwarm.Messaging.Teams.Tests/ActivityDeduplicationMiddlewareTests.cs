using AgentSwarm.Messaging.Teams.Middleware;
using AgentSwarm.Messaging.Teams.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class ActivityDeduplicationMiddlewareTests
{
    private sealed class TestMonitor : IOptionsMonitor<TeamsMessagingOptions>
    {
        public TestMonitor(TeamsMessagingOptions value) { CurrentValue = value; }
        public TeamsMessagingOptions CurrentValue { get; }
        public TeamsMessagingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TeamsMessagingOptions, string?> listener) => null;
    }

    private sealed class FakeTurnContext : ITurnContext
    {
        public FakeTurnContext(Activity activity) { Activity = activity; }
        public BotAdapter Adapter => throw new NotSupportedException();
        public TurnContextStateCollection TurnState { get; } = new();
        public Activity Activity { get; }
        public bool Responded => false;
        public Task DeleteActivityAsync(string activityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteActivityAsync(ConversationReference reference, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResourceResponse[]> SendActivitiesAsync(IActivity[] activities, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<ResourceResponse>());
        public Task<ResourceResponse> SendActivityAsync(string textReplyToSend, string? speak = null, string inputHint = "acceptingInput", CancellationToken cancellationToken = default) => Task.FromResult(new ResourceResponse());
        public Task<ResourceResponse> SendActivityAsync(IActivity activity, CancellationToken cancellationToken = default) => Task.FromResult(new ResourceResponse());
        public Task<ResourceResponse> UpdateActivityAsync(IActivity activity, CancellationToken cancellationToken = default) => Task.FromResult(new ResourceResponse());
        public ITurnContext OnSendActivities(SendActivitiesHandler handler) => this;
        public ITurnContext OnUpdateActivity(UpdateActivityHandler handler) => this;
        public ITurnContext OnDeleteActivity(DeleteActivityHandler handler) => this;
    }

    [Fact]
    public async Task First_Activity_Passes_Through()
    {
        var store = new InMemoryActivityIdStore(new TestMonitor(new TeamsMessagingOptions { DeduplicationTtlMinutes = 10 }), clock: null, enableBackgroundEviction: false);
        var middleware = new ActivityDeduplicationMiddleware(store, NullLogger<ActivityDeduplicationMiddleware>.Instance);
        var turn = new FakeTurnContext(new Activity { Id = "a1", Type = "message" });

        var nextCalled = false;
        await middleware.OnTurnAsync(turn, _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Duplicate_Activity_Suppressed()
    {
        var store = new InMemoryActivityIdStore(new TestMonitor(new TeamsMessagingOptions { DeduplicationTtlMinutes = 10 }), clock: null, enableBackgroundEviction: false);
        var middleware = new ActivityDeduplicationMiddleware(store, NullLogger<ActivityDeduplicationMiddleware>.Instance);

        await middleware.OnTurnAsync(new FakeTurnContext(new Activity { Id = "a1", Type = "message" }), _ => Task.CompletedTask);

        var nextCalled = false;
        await middleware.OnTurnAsync(new FakeTurnContext(new Activity { Id = "a1", Type = "message" }), _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Missing_Activity_Id_Falls_Through()
    {
        var store = new InMemoryActivityIdStore(new TestMonitor(new TeamsMessagingOptions { DeduplicationTtlMinutes = 10 }), clock: null, enableBackgroundEviction: false);
        var middleware = new ActivityDeduplicationMiddleware(store, NullLogger<ActivityDeduplicationMiddleware>.Instance);

        var nextCalled = false;
        await middleware.OnTurnAsync(new FakeTurnContext(new Activity { Type = "message" }), _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.True(nextCalled);
    }
}
