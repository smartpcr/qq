using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Abstractions.Tests;

public class MessengerMessageTests
{
    [Fact]
    public void Roundtrip_Preserves_All_Fields()
    {
        var original = new MessengerMessage
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            AgentId = "agent-build",
            TaskId = "task-1",
            CorrelationId = "corr-1",
            Body = "deploy started",
            Timestamp = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
            Severity = MessageSeverities.Info
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<MessengerMessage>(json);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void Default_Severity_Is_Info()
    {
        var m = new MessengerMessage();
        Assert.Equal(MessageSeverities.Info, m.Severity);
    }
}

public class HumanActionTests
{
    [Fact]
    public void Roundtrip_Preserves_All_Fields()
    {
        var original = new HumanAction
        {
            ActionId = "a1",
            Label = "Approve",
            Value = "approve",
            RequiresComment = false
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<HumanAction>(json);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void RequiresComment_Defaults_False()
    {
        var a = new HumanAction();
        Assert.False(a.RequiresComment);
    }
}
