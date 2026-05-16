using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class MessengerMessageTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void Roundtrip_PreservesAllFields_IncludingMetadata()
    {
        var message = new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1122334455",
            Body: "Deploy succeeded.",
            Severity: MessageSeverity.Normal,
            CorrelationId: "trace-1",
            Timestamp: new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero),
            Metadata: new Dictionary<string, string>
            {
                ["ThreadId"] = "9988776655",
                ["AgentId"] = "deploy-agent",
            });

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<MessengerMessage>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Messenger.Should().Be("Discord");
        roundTripped.ChannelId.Should().Be("1122334455");
        roundTripped.Body.Should().Be("Deploy succeeded.");
        roundTripped.Severity.Should().Be(MessageSeverity.Normal);
        roundTripped.CorrelationId.Should().Be("trace-1");
        roundTripped.Timestamp.Should().Be(message.Timestamp);
        roundTripped.Metadata.Should().NotBeNull();
        roundTripped.Metadata!["ThreadId"].Should().Be("9988776655");
        roundTripped.Metadata["AgentId"].Should().Be("deploy-agent");
    }

    [Fact]
    public void Roundtrip_NullMetadata_IsPreserved()
    {
        var message = new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1",
            Body: "hello",
            Severity: MessageSeverity.Low,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<MessengerMessage>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Metadata.Should().BeNull();
    }

    [Fact]
    public void Severity_IsSerializedAsStringName()
    {
        var message = new MessengerMessage(
            Messenger: "Discord",
            ChannelId: "1",
            Body: "critical alert",
            Severity: MessageSeverity.Critical,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(message, JsonOptions);

        json.Should().Contain("\"Severity\":\"Critical\"");
        json.Should().NotContain("\"Severity\":0");
    }
}
