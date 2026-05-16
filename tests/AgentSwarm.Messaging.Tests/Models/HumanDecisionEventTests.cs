using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class HumanDecisionEventTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void Roundtrip_WithComment_PreservesAllFields()
    {
        var original = new HumanDecisionEvent(
            Messenger: "Discord",
            ExternalUserId: "123456789012345678",
            ExternalMessageId: "987654321098765432",
            QuestionId: "Q-42",
            SelectedActionId: "reject",
            ActionValue: "rejected",
            CorrelationId: "trace-xyz",
            Timestamp: new DateTimeOffset(2026, 5, 15, 19, 44, 48, TimeSpan.FromHours(-7)),
            Comment: "Need more context on the deployment window.");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<HumanDecisionEvent>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Messenger.Should().Be("Discord");
        roundTripped.ExternalUserId.Should().Be(original.ExternalUserId);
        roundTripped.ExternalMessageId.Should().Be(original.ExternalMessageId);
        roundTripped.QuestionId.Should().Be(original.QuestionId);
        roundTripped.SelectedActionId.Should().Be(original.SelectedActionId);
        roundTripped.ActionValue.Should().Be(original.ActionValue);
        roundTripped.CorrelationId.Should().Be(original.CorrelationId);
        roundTripped.Timestamp.Should().Be(original.Timestamp);
        roundTripped.Comment.Should().Be(original.Comment);
    }

    [Fact]
    public void Roundtrip_WithoutComment_DefaultsToNull()
    {
        var original = new HumanDecisionEvent(
            Messenger: "Discord",
            ExternalUserId: "1",
            ExternalMessageId: "2",
            QuestionId: "Q-1",
            SelectedActionId: "approve",
            ActionValue: "approved",
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        original.Comment.Should().BeNull();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<HumanDecisionEvent>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Comment.Should().BeNull();
    }

    [Fact]
    public void Json_ContainsCommentProperty_WhenSet()
    {
        var decision = new HumanDecisionEvent(
            Messenger: "Discord",
            ExternalUserId: "u",
            ExternalMessageId: "m",
            QuestionId: "Q",
            SelectedActionId: "a",
            ActionValue: "v",
            CorrelationId: "c",
            Timestamp: DateTimeOffset.UnixEpoch,
            Comment: "rationale text");

        var json = JsonSerializer.Serialize(decision, JsonOptions);

        json.Should().Contain("\"Comment\":\"rationale text\"");
    }

    [Fact]
    public void ExistingPositionalCallers_RemainSourceCompatible_AfterCommentAddition()
    {
        // The Comment parameter must be the last positional parameter and have a
        // default value of null so existing callers that did not pass Comment
        // continue to compile unchanged. This test exercises that calling shape.
        var decision = new HumanDecisionEvent(
            "Discord",
            "u",
            "m",
            "Q-1",
            "approve",
            "approved",
            "trace",
            DateTimeOffset.UnixEpoch);

        decision.Comment.Should().BeNull();
        decision.QuestionId.Should().Be("Q-1");
    }
}
