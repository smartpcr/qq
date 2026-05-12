using System.Text.Json;
using FluentAssertions;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;

namespace AgentSwarm.Messaging.Tests;

public class SharedDataModelTests
{
    [Fact]
    public void MessengerMessage_NullCorrelationId_ThrowsArgumentNullException()
    {
        var act = () => new MessengerMessage
        {
            MessageId = "msg-1",
            CorrelationId = null!,
            ConversationId = "conv-1",
            Timestamp = DateTimeOffset.UtcNow,
            Text = "hello",
            Severity = MessageSeverity.Normal
        };

        act.Should().Throw<ArgumentNullException>()
           .And.ParamName.Should().Be("CorrelationId");
    }

    [Fact]
    public void MessengerMessage_ValidCorrelationId_DoesNotThrow()
    {
        var act = () => new MessengerMessage
        {
            MessageId = "msg-1",
            CorrelationId = "trace-abc",
            ConversationId = "conv-1",
            Timestamp = DateTimeOffset.UtcNow,
            Text = "hello",
            Severity = MessageSeverity.Normal
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void AgentQuestionEnvelope_SerializationRoundTrip_PreservesAllFields()
    {
        var question = new AgentQuestion
        {
            QuestionId = "Q-42",
            AgentId = "build-agent-3",
            TaskId = "TASK-100",
            Title = "Approve release?",
            Body = "Solution12 build completed. Ready to publish.",
            Severity = MessageSeverity.High,
            AllowedActions = new[]
            {
                new HumanAction
                {
                    ActionId = "approve",
                    Label = "Approve",
                    Value = "approve",
                    RequiresComment = false
                },
                new HumanAction
                {
                    ActionId = "reject",
                    Label = "Reject",
                    Value = "reject",
                    RequiresComment = true
                }
            },
            ExpiresAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
            CorrelationId = "trace-xyz"
        };

        var envelope = new AgentQuestionEnvelope
        {
            Question = question,
            ProposedDefaultActionId = "approve",
            RoutingMetadata = new Dictionary<string, string>
            {
                ["TelegramChatId"] = "123456"
            }
        };

        var json = JsonSerializer.Serialize(envelope);
        var deserialized = JsonSerializer.Deserialize<AgentQuestionEnvelope>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Question.QuestionId.Should().Be("Q-42");
        deserialized.Question.AgentId.Should().Be("build-agent-3");
        deserialized.Question.TaskId.Should().Be("TASK-100");
        deserialized.Question.Title.Should().Be("Approve release?");
        deserialized.Question.Body.Should().Be("Solution12 build completed. Ready to publish.");
        deserialized.Question.Severity.Should().Be(MessageSeverity.High);
        deserialized.Question.AllowedActions.Should().HaveCount(2);
        deserialized.Question.AllowedActions[0].ActionId.Should().Be("approve");
        deserialized.Question.AllowedActions[0].Label.Should().Be("Approve");
        deserialized.Question.AllowedActions[0].Value.Should().Be("approve");
        deserialized.Question.AllowedActions[0].RequiresComment.Should().BeFalse();
        deserialized.Question.AllowedActions[1].ActionId.Should().Be("reject");
        deserialized.Question.AllowedActions[1].RequiresComment.Should().BeTrue();
        deserialized.Question.ExpiresAt.Should().Be(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        deserialized.Question.CorrelationId.Should().Be("trace-xyz");
        deserialized.ProposedDefaultActionId.Should().Be("approve");
        deserialized.RoutingMetadata.Should().ContainKey("TelegramChatId")
            .WhoseValue.Should().Be("123456");
    }

    [Fact]
    public void AgentQuestionEnvelope_NullProposedDefaultActionId_RoundTrips()
    {
        var envelope = new AgentQuestionEnvelope
        {
            Question = CreateMinimalQuestion(),
            ProposedDefaultActionId = null
        };

        var json = JsonSerializer.Serialize(envelope);
        var deserialized = JsonSerializer.Deserialize<AgentQuestionEnvelope>(json);

        deserialized.Should().NotBeNull();
        deserialized!.ProposedDefaultActionId.Should().BeNull();
    }

    [Fact]
    public void HumanDecisionEvent_IsImmutableRecord()
    {
        var evt = new HumanDecisionEvent
        {
            QuestionId = "Q-1",
            ActionValue = "approve",
            Messenger = "Telegram",
            ExternalUserId = "user-1",
            ExternalMessageId = "msg-1",
            ReceivedAt = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1"
        };

        // Record with-expression produces a new instance, original unchanged
        var modified = evt with { ActionValue = "reject" };

        modified.ActionValue.Should().Be("reject");
        evt.ActionValue.Should().Be("approve");
    }

    [Fact]
    public void AgentQuestion_IsImmutableRecord()
    {
        var question = new AgentQuestion
        {
            QuestionId = "Q-1",
            AgentId = "agent-1",
            TaskId = "task-1",
            Title = "Original title",
            Body = "Original body",
            Severity = MessageSeverity.Normal,
            AllowedActions = new[]
            {
                new HumanAction
                {
                    ActionId = "approve",
                    Label = "Approve",
                    Value = "approve",
                    RequiresComment = false
                }
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CorrelationId = "trace-1"
        };

        // Record with-expression produces a new instance, original unchanged
        var modified = question with { Title = "Modified title" };

        modified.Title.Should().Be("Modified title");
        question.Title.Should().Be("Original title");

        // All init-only properties are preserved on copy
        modified.QuestionId.Should().Be("Q-1");
        modified.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void OutboundMessage_DefaultStatus_IsPending()
    {
        var msg = new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = "q:agent-1:Q-1",
            ChatId = 12345L,
            Payload = "{}",
            Severity = MessageSeverity.Normal,
            SourceType = OutboundSourceType.Question,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1"
        };

        msg.Status.Should().Be(OutboundMessageStatus.Pending);
        msg.AttemptCount.Should().Be(0);
        msg.MaxAttempts.Should().Be(5);
    }

    [Fact]
    public void TaskOversight_RecordProperties_AreAccessible()
    {
        var oversight = new TaskOversight
        {
            TaskId = "TASK-1",
            OperatorBindingId = Guid.NewGuid(),
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = "op-1",
            CorrelationId = "trace-1"
        };

        oversight.TaskId.Should().Be("TASK-1");
        oversight.AssignedBy.Should().Be("op-1");
    }

    [Fact]
    public void InboundUpdate_DefaultAttemptCount_IsZero()
    {
        var update = new InboundUpdate
        {
            UpdateId = 100L,
            RawPayload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            IdempotencyStatus = IdempotencyStatus.Received
        };

        update.AttemptCount.Should().Be(0);
        update.ProcessedAt.Should().BeNull();
        update.ErrorDetail.Should().BeNull();
    }

    [Fact]
    public void MessengerEvent_AllProperties_RoundTrip()
    {
        var evt = new MessengerEvent
        {
            EventId = "evt-1",
            EventType = EventType.Command,
            RawCommand = "/start",
            UserId = "user-1",
            ChatId = "chat-1",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1",
            Payload = "extra"
        };

        evt.EventType.Should().Be(EventType.Command);
        evt.RawCommand.Should().Be("/start");
    }

    [Fact]
    public void MessageSeverity_HasFourValues()
    {
        Enum.GetValues<MessageSeverity>().Should().HaveCount(4);
    }

    [Fact]
    public void EventType_HasFourValues()
    {
        Enum.GetValues<EventType>().Should().HaveCount(4);
    }

    private static AgentQuestion CreateMinimalQuestion() => new()
    {
        QuestionId = "Q-min",
        AgentId = "agent-1",
        TaskId = "task-1",
        Title = "Test",
        Body = "Test body",
        Severity = MessageSeverity.Normal,
        AllowedActions = Array.Empty<HumanAction>(),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        CorrelationId = "trace-min"
    };
}
