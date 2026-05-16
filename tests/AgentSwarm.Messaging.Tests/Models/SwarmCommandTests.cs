using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class SwarmCommandTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void Roundtrip_PreservesAllFields_IncludingArguments()
    {
        var command = new SwarmCommand(
            CommandId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            CommandType: "approve",
            AgentTarget: "deploy-agent",
            Arguments: new Dictionary<string, string>
            {
                ["taskId"] = "T-42",
                ["reason"] = "verified by ops",
            },
            CorrelationId: "trace-cmd",
            Timestamp: new DateTimeOffset(2026, 5, 15, 11, 22, 33, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(command, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<SwarmCommand>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.CommandId.Should().Be(command.CommandId);
        roundTripped.CommandType.Should().Be("approve");
        roundTripped.AgentTarget.Should().Be("deploy-agent");
        roundTripped.Arguments.Should().ContainKey("taskId")
            .WhoseValue.Should().Be("T-42");
        roundTripped.Arguments.Should().ContainKey("reason")
            .WhoseValue.Should().Be("verified by ops");
        roundTripped.CorrelationId.Should().Be("trace-cmd");
        roundTripped.Timestamp.Should().Be(command.Timestamp);
    }

    [Fact]
    public void Arguments_AreCaseSensitive()
    {
        var command = new SwarmCommand(
            CommandId: Guid.NewGuid(),
            CommandType: "ask",
            AgentTarget: "agent",
            Arguments: new Dictionary<string, string>
            {
                ["TaskId"] = "upper",
                ["taskId"] = "lower",
            },
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        command.Arguments.Should().HaveCount(2);
        command.Arguments["TaskId"].Should().Be("upper");
        command.Arguments["taskId"].Should().Be("lower");
    }
}
