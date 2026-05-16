using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class MessengerEventTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var ev = new MessengerEvent(
            Messenger: "Discord",
            EventType: MessengerEventType.ButtonClick,
            ExternalUserId: "user-1",
            ExternalChannelId: "channel-1",
            ExternalMessageId: "msg-1",
            Payload: "{\"customId\":\"q:Q-1:approve\"}",
            CorrelationId: "trace",
            Timestamp: new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero),
            Metadata: new Dictionary<string, string>
            {
                ["GuildId"] = "g-1",
                ["InteractionToken"] = "tok",
            });

        var json = JsonSerializer.Serialize(ev, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<MessengerEvent>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Messenger.Should().Be("Discord");
        roundTripped.EventType.Should().Be(MessengerEventType.ButtonClick);
        roundTripped.ExternalUserId.Should().Be("user-1");
        roundTripped.ExternalChannelId.Should().Be("channel-1");
        roundTripped.ExternalMessageId.Should().Be("msg-1");
        roundTripped.Payload.Should().Be("{\"customId\":\"q:Q-1:approve\"}");
        roundTripped.CorrelationId.Should().Be("trace");
        roundTripped.Timestamp.Should().Be(ev.Timestamp);
        roundTripped.Metadata.Should().NotBeNull();
        roundTripped.Metadata!["GuildId"].Should().Be("g-1");
        roundTripped.Metadata["InteractionToken"].Should().Be("tok");
    }

    [Fact]
    public void Roundtrip_OptionalFieldsNull_IsPreserved()
    {
        var ev = new MessengerEvent(
            Messenger: "Discord",
            EventType: MessengerEventType.Command,
            ExternalUserId: "u",
            ExternalChannelId: "c",
            ExternalMessageId: "m",
            Payload: null,
            CorrelationId: "trace",
            Timestamp: DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(ev, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<MessengerEvent>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Payload.Should().BeNull();
        roundTripped.Metadata.Should().BeNull();
    }

    [Theory]
    [InlineData(MessengerEventType.Command, "Command")]
    [InlineData(MessengerEventType.ButtonClick, "ButtonClick")]
    [InlineData(MessengerEventType.SelectMenu, "SelectMenu")]
    [InlineData(MessengerEventType.ModalSubmit, "ModalSubmit")]
    [InlineData(MessengerEventType.Message, "Message")]
    public void EventType_IsSerializedAsStringName(MessengerEventType type, string expected)
    {
        var ev = new MessengerEvent(
            Messenger: "Discord",
            EventType: type,
            ExternalUserId: "u",
            ExternalChannelId: "c",
            ExternalMessageId: "m",
            Payload: null,
            CorrelationId: "t",
            Timestamp: DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(ev, JsonOptions);

        json.Should().Contain($"\"EventType\":\"{expected}\"");
    }
}
