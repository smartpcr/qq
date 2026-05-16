using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class AgentQuestionSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void Roundtrip_PreservesAllFields_IncludingAllowedActions()
    {
        var actions = new[]
        {
            new HumanAction(
                ActionId: "approve",
                Label: "Approve",
                Value: "approved",
                RequiresComment: false),
            new HumanAction(
                ActionId: "reject",
                Label: "Reject",
                Value: "rejected",
                RequiresComment: true),
        };

        var original = new AgentQuestion(
            QuestionId: "Q-42",
            AgentId: "build-agent-3",
            TaskId: "task-789",
            Title: "Promote release to staging?",
            Body: "All smoke tests passed. Awaiting human sign-off before promotion.",
            Severity: MessageSeverity.High,
            AllowedActions: actions,
            ExpiresAt: new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.FromHours(-7)),
            CorrelationId: "trace-abc-123");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentQuestion>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.QuestionId.Should().Be(original.QuestionId);
        roundTripped.AgentId.Should().Be(original.AgentId);
        roundTripped.TaskId.Should().Be(original.TaskId);
        roundTripped.Title.Should().Be(original.Title);
        roundTripped.Body.Should().Be(original.Body);
        roundTripped.Severity.Should().Be(original.Severity);
        roundTripped.ExpiresAt.Should().Be(original.ExpiresAt);
        roundTripped.CorrelationId.Should().Be(original.CorrelationId);

        roundTripped.AllowedActions.Should().HaveCount(actions.Length);
        for (var i = 0; i < actions.Length; i++)
        {
            roundTripped.AllowedActions[i].ActionId.Should().Be(actions[i].ActionId);
            roundTripped.AllowedActions[i].Label.Should().Be(actions[i].Label);
            roundTripped.AllowedActions[i].Value.Should().Be(actions[i].Value);
            roundTripped.AllowedActions[i].RequiresComment
                .Should().Be(actions[i].RequiresComment);
        }
    }

    [Fact]
    public void Roundtrip_PreservesEmptyAllowedActions()
    {
        var original = new AgentQuestion(
            QuestionId: "Q-empty",
            AgentId: "agent-1",
            TaskId: "task-1",
            Title: "Notice",
            Body: "FYI only",
            Severity: MessageSeverity.Low,
            AllowedActions: Array.Empty<HumanAction>(),
            ExpiresAt: DateTimeOffset.UnixEpoch,
            CorrelationId: "trace-none");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentQuestion>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.AllowedActions.Should().BeEmpty();
    }
}
