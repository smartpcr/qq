using System.Diagnostics;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Adapters;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using DiagActivity = System.Diagnostics.Activity;
using BotActivity = Microsoft.Bot.Schema.Activity;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class TelemetryMiddlewareTests
{
    [Fact]
    public async Task OnTurnAsync_EmitsActivityWithExpectedTags()
    {
        var captured = new List<DiagActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TelemetryMiddleware.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new TelemetryMiddleware(
            new TestOptionsMonitor<TelemetryMiddlewareOptions>(new TelemetryMiddlewareOptions()),
            NullLogger<TelemetryMiddleware>.Instance);
        var ctx = NewContext("activity-1", "msteams-tenant", "conv-1");

        await middleware.OnTurnAsync(ctx, _ => Task.CompletedTask, default);

        var emitted = Assert.Single(captured);
        Assert.Equal("Teams.InboundActivity", emitted.OperationName);
        Assert.Equal("activity-1", emitted.GetTagItem("activity.id"));
        Assert.Equal("conv-1", emitted.GetTagItem("conversation.id"));
        Assert.Equal("msteams-tenant", emitted.GetTagItem("tenant.id"));
    }

    [Fact]
    public async Task OnTurnAsync_RethrowsAndTagsErrors_OnDownstreamException()
    {
        var captured = new List<DiagActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TelemetryMiddleware.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new TelemetryMiddleware(
            new TestOptionsMonitor<TelemetryMiddlewareOptions>(new TelemetryMiddlewareOptions()),
            NullLogger<TelemetryMiddleware>.Instance);
        var ctx = NewContext("activity-2", "tenant-x", "conv-2");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await middleware.OnTurnAsync(ctx, _ => throw new InvalidOperationException("downstream"), default));

        Assert.Single(captured);
        Assert.True((bool)(captured[0].GetTagItem("error") ?? false));
    }

    [Fact]
    public async Task OnTurnAsync_SkipsPayloadCapture_WhenDisabled()
    {
        var captured = new List<DiagActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TelemetryMiddleware.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new TelemetryMiddleware(
            new TestOptionsMonitor<TelemetryMiddlewareOptions>(new TelemetryMiddlewareOptions { EnableDetailedPayloadCapture = false }),
            NullLogger<TelemetryMiddleware>.Instance);
        var ctx = NewContext("a3", "t3", "c3", text: "secret-text");

        await middleware.OnTurnAsync(ctx, _ => Task.CompletedTask, default);

        Assert.Null(captured.Single().GetTagItem("activity.text"));
    }

    [Fact]
    public async Task OnTurnAsync_ExtractsTenantId_FromTeamsChannelData_WhenConversationTenantIdMissing()
    {
        // Real Bot Framework HTTP requests deserialize ChannelData as a Newtonsoft.Json JObject.
        // Many Teams activities populate the tenant only under ChannelData.tenant.id and leave
        // Conversation.TenantId blank — this regression test pins that fallback path so spans
        // still tag tenant.id for every inbound Teams activity (Stage 2.1 requirement).
        var captured = new List<DiagActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TelemetryMiddleware.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new TelemetryMiddleware(
            new TestOptionsMonitor<TelemetryMiddlewareOptions>(new TelemetryMiddlewareOptions()),
            NullLogger<TelemetryMiddleware>.Instance);

        var adapter = new TestAdapter();
        var channelData = Newtonsoft.Json.Linq.JObject.Parse("{\"tenant\":{\"id\":\"tenant-from-channeldata\"}}");
        var activity = new BotActivity
        {
            Type = ActivityTypes.Message,
            Id = "activity-cd",
            ChannelId = "msteams",
            ChannelData = channelData,
            // Intentionally leave Conversation.TenantId empty.
            Conversation = new ConversationAccount { Id = "conv-cd", TenantId = string.Empty },
        };
        var ctx = new TurnContext(adapter, activity);

        await middleware.OnTurnAsync(ctx, _ => Task.CompletedTask, default);

        Assert.Equal("tenant-from-channeldata", Assert.Single(captured).GetTagItem("tenant.id"));
    }

    [Fact]
    public async Task OnTurnAsync_Prefers_ConversationTenantId_Over_ChannelData()
    {
        var captured = new List<DiagActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TelemetryMiddleware.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new TelemetryMiddleware(
            new TestOptionsMonitor<TelemetryMiddlewareOptions>(new TelemetryMiddlewareOptions()),
            NullLogger<TelemetryMiddleware>.Instance);

        var adapter = new TestAdapter();
        var activity = new BotActivity
        {
            Type = ActivityTypes.Message,
            Id = "activity-both",
            ChannelId = "msteams",
            ChannelData = Newtonsoft.Json.Linq.JObject.Parse("{\"tenant\":{\"id\":\"channel-tenant\"}}"),
            Conversation = new ConversationAccount { Id = "c", TenantId = "conv-tenant" },
        };
        var ctx = new TurnContext(adapter, activity);

        await middleware.OnTurnAsync(ctx, _ => Task.CompletedTask, default);

        // Conversation.TenantId takes precedence — the channel-data path is the fallback.
        Assert.Equal("conv-tenant", Assert.Single(captured).GetTagItem("tenant.id"));
    }

    private static ITurnContext NewContext(string id, string tenantId, string conversationId, string? text = null)
    {
        var adapter = new TestAdapter();
        var activity = new BotActivity
        {
            Type = ActivityTypes.Message,
            Id = id,
            ChannelId = "msteams",
            Text = text,
            Conversation = new ConversationAccount { Id = conversationId, TenantId = tenantId },
        };
        return new TurnContext(adapter, activity);
    }
}
