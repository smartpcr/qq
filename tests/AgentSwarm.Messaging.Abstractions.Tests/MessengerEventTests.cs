using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Abstractions.Tests;

public class MessengerEventTests
{
    [Fact]
    public void CommandEvent_Roundtrips_Through_MessengerEvent_Base()
    {
        MessengerEvent original = new CommandEvent
        {
            EventType = MessengerEventTypes.AgentTaskRequest,
            Source = MessengerEventSources.PersonalChat,
            Command = "ask",
            Arguments = new[] { "agent-build", "deploy", "prod" }
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<MessengerEvent>(json);

        var cmd = Assert.IsType<CommandEvent>(deserialized);
        Assert.Equal(MessengerEventTypes.AgentTaskRequest, cmd.EventType);
        Assert.Equal(MessengerEventSources.PersonalChat, cmd.Source);
        Assert.Equal("ask", cmd.Command);
        Assert.Equal(new[] { "agent-build", "deploy", "prod" }, cmd.Arguments);
    }

    [Fact]
    public void DecisionEvent_Defaults_EventType_To_Decision()
    {
        var ev = new DecisionEvent
        {
            Source = MessengerEventSources.PersonalChat,
            Decision = new HumanDecisionEvent
            {
                QuestionId = "q-1",
                ActionValue = "approve",
                Messenger = "Teams",
                ExternalUserId = "u1",
                ExternalMessageId = "m1",
                ReceivedAt = DateTimeOffset.UtcNow,
                CorrelationId = "c1"
            }
        };

        Assert.Equal(MessengerEventTypes.Decision, ev.EventType);
    }

    [Fact]
    public void TextEvent_Defaults_EventType_To_Text()
    {
        var ev = new TextEvent
        {
            Source = MessengerEventSources.TeamChannel,
            Text = "hello"
        };

        Assert.Equal(MessengerEventTypes.Text, ev.EventType);
    }

    [Fact]
    public void DecisionEvent_Roundtrips_Through_MessengerEvent_Base()
    {
        MessengerEvent original = new DecisionEvent
        {
            Source = MessengerEventSources.PersonalChat,
            Decision = new HumanDecisionEvent
            {
                QuestionId = "q-1",
                ActionValue = "reject",
                Comment = "missing test coverage",
                Messenger = "Teams",
                ExternalUserId = "u1",
                ExternalMessageId = "m1",
                ReceivedAt = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
                CorrelationId = "corr-1"
            }
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<MessengerEvent>(json);

        var decision = Assert.IsType<DecisionEvent>(deserialized);
        Assert.Equal("reject", decision.Decision.ActionValue);
        Assert.Equal("missing test coverage", decision.Decision.Comment);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void TextEvent_Roundtrips_Through_MessengerEvent_Base()
    {
        MessengerEvent original = new TextEvent
        {
            Source = MessengerEventSources.MessageAction,
            Text = "please retry"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<MessengerEvent>(json);

        var text = Assert.IsType<TextEvent>(deserialized);
        Assert.Equal(MessengerEventTypes.Text, text.EventType);
        Assert.Equal(MessengerEventSources.MessageAction, text.Source);
        Assert.Equal("please retry", text.Text);
    }

    [Fact]
    public void Canonical_Vocabulary_Constants_Match_Architecture_Doc()
    {
        // architecture.md §3.1 canonical event-type vocabulary
        Assert.Equal("AgentTaskRequest", MessengerEventTypes.AgentTaskRequest);
        Assert.Equal("Command", MessengerEventTypes.Command);
        Assert.Equal("Escalation", MessengerEventTypes.Escalation);
        Assert.Equal("PauseAgent", MessengerEventTypes.PauseAgent);
        Assert.Equal("ResumeAgent", MessengerEventTypes.ResumeAgent);
        Assert.Equal("Decision", MessengerEventTypes.Decision);
        Assert.Equal("Text", MessengerEventTypes.Text);
        Assert.Equal("InstallUpdate", MessengerEventTypes.InstallUpdate);
        Assert.Equal("Reaction", MessengerEventTypes.Reaction);

        // Source vocabulary
        Assert.Equal("PersonalChat", MessengerEventSources.PersonalChat);
        Assert.Equal("TeamChannel", MessengerEventSources.TeamChannel);
        Assert.Equal("MessageAction", MessengerEventSources.MessageAction);

        // AgentQuestion status vocabulary
        Assert.Equal("Open", AgentQuestionStatuses.Open);
        Assert.Equal("Resolved", AgentQuestionStatuses.Resolved);
        Assert.Equal("Expired", AgentQuestionStatuses.Expired);

        // Severity vocabulary
        Assert.Equal("Info", MessageSeverities.Info);
        Assert.Equal("Warning", MessageSeverities.Warning);
        Assert.Equal("Error", MessageSeverities.Error);
        Assert.Equal("Critical", MessageSeverities.Critical);
    }
}
