using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Adapters;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class ActivityDeduplicationMiddlewareTests
{
    [Fact]
    public async Task DistinctActivities_AreForwardedToNext()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);
        var middleware = new ActivityDeduplicationMiddleware(store, NullLogger<ActivityDeduplicationMiddleware>.Instance);
        var (context, nextCount) = NewContext("activity-1");

        await middleware.OnTurnAsync(context, _ => { nextCount.Increment(); return Task.CompletedTask; }, default);

        Assert.Equal(1, nextCount.Value);
    }

    [Fact]
    public async Task RepeatedActivity_IsSuppressed()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);
        var middleware = new ActivityDeduplicationMiddleware(store, NullLogger<ActivityDeduplicationMiddleware>.Instance);
        var nextCount = new Counter();

        var (c1, _) = NewContext("dup-id");
        var (c2, _) = NewContext("dup-id");
        await middleware.OnTurnAsync(c1, _ => { nextCount.Increment(); return Task.CompletedTask; }, default);
        await middleware.OnTurnAsync(c2, _ => { nextCount.Increment(); return Task.CompletedTask; }, default);

        Assert.Equal(1, nextCount.Value);
    }

    [Fact]
    public async Task ReplyToId_TakesPrecedenceForInvokeDedup()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);
        var middleware = new ActivityDeduplicationMiddleware(store, NullLogger<ActivityDeduplicationMiddleware>.Instance);
        var nextCount = new Counter();

        var (c1, _) = NewContext(activityId: "invoke-1", replyToId: "card-A", type: ActivityTypes.Invoke);
        var (c2, _) = NewContext(activityId: "invoke-2", replyToId: "card-A", type: ActivityTypes.Invoke);

        await middleware.OnTurnAsync(c1, _ => { nextCount.Increment(); return Task.CompletedTask; }, default);
        await middleware.OnTurnAsync(c2, _ => { nextCount.Increment(); return Task.CompletedTask; }, default);

        Assert.Equal(1, nextCount.Value);
    }

    [Fact]
    public async Task NonInvokeActivities_WithSharedReplyToId_AreNotDeduplicated()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);
        var middleware = new ActivityDeduplicationMiddleware(store, NullLogger<ActivityDeduplicationMiddleware>.Instance);
        var nextCount = new Counter();

        // Two distinct user message replies in the same thread, both pointing at the same
        // parent message via ReplyToId. The user IS sending two separate messages — they
        // must both reach the handler.
        var (c1, _) = NewContext(activityId: "msg-a", replyToId: "thread-parent", type: ActivityTypes.Message);
        var (c2, _) = NewContext(activityId: "msg-b", replyToId: "thread-parent", type: ActivityTypes.Message);

        await middleware.OnTurnAsync(c1, _ => { nextCount.Increment(); return Task.CompletedTask; }, default);
        await middleware.OnTurnAsync(c2, _ => { nextCount.Increment(); return Task.CompletedTask; }, default);

        Assert.Equal(2, nextCount.Value);
    }

    [Fact]
    public async Task ActivityWithNoId_IsForwarded()
    {
        using var store = new InMemoryActivityIdStore(ttlMinutes: 10);
        var middleware = new ActivityDeduplicationMiddleware(store, NullLogger<ActivityDeduplicationMiddleware>.Instance);
        var (context, nextCount) = NewContext(activityId: null);

        await middleware.OnTurnAsync(context, _ => { nextCount.Increment(); return Task.CompletedTask; }, default);

        Assert.Equal(1, nextCount.Value);
    }

    private static (ITurnContext context, Counter nextCount) NewContext(string? activityId, string? replyToId = null, string type = ActivityTypes.Message)
    {
        var adapter = new TestAdapter();
        var activity = new Activity
        {
            Type = type,
            Id = activityId,
            ReplyToId = replyToId,
            Conversation = new ConversationAccount { Id = "conv-1" },
            ChannelId = "msteams",
        };
        var context = new TurnContext(adapter, activity);
        return (context, new Counter());
    }

    private sealed class Counter
    {
        private int _value;
        public int Value => _value;
        public void Increment() => Interlocked.Increment(ref _value);
    }
}
