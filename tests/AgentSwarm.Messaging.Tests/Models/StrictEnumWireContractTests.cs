using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

/// <summary>
/// Pins the "names-only" JSON wire contract for the shared messenger enums.
/// The built-in <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// writes member names but happily accepts numeric tokens (e.g. <c>1</c>) and even
/// numeric-string tokens (e.g. <c>"1"</c>) on read. That breaks the cross-connector
/// contract: a future re-ordering of enum members would silently re-map values
/// persisted by older producers. These tests verify the custom strict converter
/// rejects every form except a case-sensitive declared member name.
/// </summary>
public class StrictEnumWireContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private sealed record SeverityHolder(MessageSeverity Severity);
    private sealed record ChannelPurposeHolder(ChannelPurpose Purpose);
    private sealed record EventTypeHolder(MessengerEventType EventType);

    // ---- MessageSeverity ----

    [Theory]
    [InlineData("Critical")]
    [InlineData("High")]
    [InlineData("Normal")]
    [InlineData("Low")]
    public void MessageSeverity_RoundTripsCanonicalMemberName(string name)
    {
        var json = $"{{\"Severity\":\"{name}\"}}";

        var deserialized = JsonSerializer.Deserialize<SeverityHolder>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Severity.ToString().Should().Be(name);
    }

    [Fact]
    public void MessageSeverity_WritesMemberNameNotNumber()
    {
        var holder = new SeverityHolder(MessageSeverity.High);

        var json = JsonSerializer.Serialize(holder, JsonOptions);

        json.Should().Be("{\"Severity\":\"High\"}");
    }

    [Theory]
    [InlineData("{\"Severity\":1}")]              // numeric token
    [InlineData("{\"Severity\":0}")]              // numeric token (zero / Critical)
    [InlineData("{\"Severity\":99}")]             // out-of-range numeric token
    [InlineData("{\"Severity\":\"1\"}")]          // numeric-string token
    [InlineData("{\"Severity\":\"high\"}")]       // case-mismatched name
    [InlineData("{\"Severity\":\"HIGH\"}")]       // case-mismatched name
    [InlineData("{\"Severity\":\"Critical,Low\"}")] // flag-combined string
    [InlineData("{\"Severity\":\"NotAMember\"}")]  // unknown name
    [InlineData("{\"Severity\":\"\"}")]           // empty string
    [InlineData("{\"Severity\":null}")]           // null
    [InlineData("{\"Severity\":true}")]           // wrong token type
    public void MessageSeverity_DeserializationRejectsNonCanonicalForms(string json)
    {
        var act = () => JsonSerializer.Deserialize<SeverityHolder>(json, JsonOptions);

        act.Should().Throw<JsonException>();
    }

    // ---- ChannelPurpose ----

    [Theory]
    [InlineData("Control")]
    [InlineData("Alert")]
    [InlineData("Workstream")]
    public void ChannelPurpose_RoundTripsCanonicalMemberName(string name)
    {
        var json = $"{{\"Purpose\":\"{name}\"}}";

        var deserialized = JsonSerializer.Deserialize<ChannelPurposeHolder>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Purpose.ToString().Should().Be(name);
    }

    [Theory]
    [InlineData("{\"Purpose\":0}")]
    [InlineData("{\"Purpose\":1}")]
    [InlineData("{\"Purpose\":\"0\"}")]
    [InlineData("{\"Purpose\":\"control\"}")]
    [InlineData("{\"Purpose\":\"Ghost\"}")]
    public void ChannelPurpose_DeserializationRejectsNonCanonicalForms(string json)
    {
        var act = () => JsonSerializer.Deserialize<ChannelPurposeHolder>(json, JsonOptions);

        act.Should().Throw<JsonException>();
    }

    // ---- MessengerEventType ----

    [Theory]
    [InlineData("Command")]
    [InlineData("ButtonClick")]
    [InlineData("SelectMenu")]
    [InlineData("ModalSubmit")]
    [InlineData("Message")]
    public void MessengerEventType_RoundTripsCanonicalMemberName(string name)
    {
        var json = $"{{\"EventType\":\"{name}\"}}";

        var deserialized = JsonSerializer.Deserialize<EventTypeHolder>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.EventType.ToString().Should().Be(name);
    }

    [Theory]
    [InlineData("{\"EventType\":0}")]
    [InlineData("{\"EventType\":\"0\"}")]
    [InlineData("{\"EventType\":\"command\"}")]
    [InlineData("{\"EventType\":\"unknown\"}")]
    public void MessengerEventType_DeserializationRejectsNonCanonicalForms(string json)
    {
        var act = () => JsonSerializer.Deserialize<EventTypeHolder>(json, JsonOptions);

        act.Should().Throw<JsonException>();
    }

    // ---- Write-side strictness: undefined enum values must not serialize ----

    [Fact]
    public void MessageSeverity_SerializationRejectsUndefinedValue()
    {
        // value.ToString() would emit "99" -- a number string -- creating a
        // write/read asymmetry where Write succeeds and Read then rejects.
        var holder = new SeverityHolder((MessageSeverity)99);

        var act = () => JsonSerializer.Serialize(holder, JsonOptions);

        act.Should().Throw<JsonException>()
            .WithMessage("*undefined*");
    }

    [Fact]
    public void ChannelPurpose_SerializationRejectsUndefinedValue()
    {
        var holder = new ChannelPurposeHolder((ChannelPurpose)42);

        var act = () => JsonSerializer.Serialize(holder, JsonOptions);

        act.Should().Throw<JsonException>()
            .WithMessage("*undefined*");
    }

    [Fact]
    public void MessengerEventType_SerializationRejectsUndefinedValue()
    {
        var holder = new EventTypeHolder((MessengerEventType)99);

        var act = () => JsonSerializer.Serialize(holder, JsonOptions);

        act.Should().Throw<JsonException>()
            .WithMessage("*undefined*");
    }
}
