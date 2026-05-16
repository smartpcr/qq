using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class AgentQuestionEnvelopeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private static AgentQuestion SampleQuestion() =>
        new(
            QuestionId: "Q-env",
            AgentId: "agent-1",
            TaskId: "task-1",
            Title: "Title",
            Body: "Body",
            Severity: MessageSeverity.Normal,
            AllowedActions: new[]
            {
                new HumanAction("ok", "OK", "ok", false),
                new HumanAction("cancel", "Cancel", "cancelled", false),
            },
            ExpiresAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            CorrelationId: "trace");

    [Fact]
    public void Roundtrip_PreservesQuestion_DefaultActionId_AndRoutingMetadata()
    {
        var routing = new Dictionary<string, string>
        {
            ["DiscordChannelId"] = "1122334455",
            ["DiscordThreadId"] = "9988776655",
            ["AgentRole"] = "release-manager",
        };

        var envelope = new AgentQuestionEnvelope(
            Question: SampleQuestion(),
            ProposedDefaultActionId: "ok",
            RoutingMetadata: routing);

        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentQuestionEnvelope>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Question.QuestionId.Should().Be("Q-env");
        roundTripped.Question.AllowedActions.Should().HaveCount(2);
        roundTripped.ProposedDefaultActionId.Should().Be("ok");
        roundTripped.RoutingMetadata.Should().ContainKey("DiscordChannelId")
            .WhoseValue.Should().Be("1122334455");
        roundTripped.RoutingMetadata.Should().ContainKey("DiscordThreadId")
            .WhoseValue.Should().Be("9988776655");
        roundTripped.RoutingMetadata.Should().ContainKey("AgentRole")
            .WhoseValue.Should().Be("release-manager");
    }

    [Fact]
    public void Roundtrip_NullProposedDefaultActionId_IsPreserved()
    {
        var envelope = new AgentQuestionEnvelope(
            Question: SampleQuestion(),
            ProposedDefaultActionId: null,
            RoutingMetadata: new Dictionary<string, string>
            {
                ["DiscordChannelId"] = "42",
            });

        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentQuestionEnvelope>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.ProposedDefaultActionId.Should().BeNull();
        roundTripped.RoutingMetadata.Should().HaveCount(1);
    }

    [Fact]
    public void RoutingMetadata_KeysAreCaseSensitive()
    {
        var envelope = new AgentQuestionEnvelope(
            Question: SampleQuestion(),
            ProposedDefaultActionId: null,
            RoutingMetadata: new Dictionary<string, string>
            {
                ["DiscordChannelId"] = "1",
                ["discordchannelid"] = "2",
            });

        envelope.RoutingMetadata.Should().HaveCount(2);
        envelope.RoutingMetadata["DiscordChannelId"].Should().Be("1");
        envelope.RoutingMetadata["discordchannelid"].Should().Be("2");
    }
}
