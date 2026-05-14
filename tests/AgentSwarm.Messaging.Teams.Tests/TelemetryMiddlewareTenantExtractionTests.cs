using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class TelemetryMiddlewareTenantExtractionTests
{
    [Fact]
    public void ExtractTenantId_Prefers_Conversation_TenantId()
    {
        var activity = new Activity
        {
            Type = "message",
            Conversation = new ConversationAccount { Id = "c", TenantId = "from-conv" },
        };
        Assert.Equal("from-conv", TelemetryMiddleware.ExtractTenantId(activity));
    }

    [Fact]
    public void ExtractTenantId_Falls_Back_To_ChannelData_When_Conversation_TenantId_Missing()
    {
        var activity = new Activity
        {
            Type = "message",
            Conversation = new ConversationAccount { Id = "c", TenantId = string.Empty },
            ChannelData = JObject.Parse(@"{ ""tenant"": { ""id"": ""from-channel-data"" } }"),
        };
        Assert.Equal("from-channel-data", TelemetryMiddleware.ExtractTenantId(activity));
    }

    [Fact]
    public void ExtractTenantId_Returns_Empty_When_Neither_Present()
    {
        var activity = new Activity
        {
            Type = "message",
            Conversation = new ConversationAccount { Id = "c" },
        };
        Assert.Equal(string.Empty, TelemetryMiddleware.ExtractTenantId(activity));
    }

    [Fact]
    public void ExtractTenantId_Handles_Null_Activity()
    {
        Assert.Equal(string.Empty, TelemetryMiddleware.ExtractTenantId(null));
    }
}
