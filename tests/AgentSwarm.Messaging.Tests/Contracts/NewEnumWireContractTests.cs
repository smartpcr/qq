using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Contracts;

/// <summary>
/// Pins the names-only JSON wire contract for the new Stage 1.3 enums.
/// Mirrors the existing <see cref="StrictEnumWireContractTests"/> coverage so
/// the same cross-connector / cross-restart guarantees apply.
/// </summary>
public class NewEnumWireContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private sealed record StatusHolder(OutboundMessageStatus Status);
    private sealed record SourceHolder(OutboundMessageSource SourceType);
    private sealed record QuestionStatusHolder(PendingQuestionStatus Status);

    [Theory]
    [InlineData(OutboundMessageStatus.Pending, "Pending")]
    [InlineData(OutboundMessageStatus.Sending, "Sending")]
    [InlineData(OutboundMessageStatus.Sent, "Sent")]
    [InlineData(OutboundMessageStatus.Failed, "Failed")]
    [InlineData(OutboundMessageStatus.DeadLettered, "DeadLettered")]
    public void OutboundMessageStatus_RoundTripsAsName(OutboundMessageStatus value, string name)
    {
        var json = JsonSerializer.Serialize(new StatusHolder(value), JsonOptions);
        json.Should().Contain($"\"Status\":\"{name}\"");
        var back = JsonSerializer.Deserialize<StatusHolder>(json, JsonOptions);
        back!.Status.Should().Be(value);
    }

    [Fact]
    public void OutboundMessageStatus_NumericToken_Rejected()
    {
        var act = () => JsonSerializer.Deserialize<StatusHolder>("{\"Status\":1}", JsonOptions);
        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(OutboundMessageSource.Question, "Question")]
    [InlineData(OutboundMessageSource.Alert, "Alert")]
    [InlineData(OutboundMessageSource.StatusUpdate, "StatusUpdate")]
    [InlineData(OutboundMessageSource.CommandAck, "CommandAck")]
    public void OutboundMessageSource_RoundTripsAsName(OutboundMessageSource value, string name)
    {
        var json = JsonSerializer.Serialize(new SourceHolder(value), JsonOptions);
        json.Should().Contain($"\"SourceType\":\"{name}\"");
        var back = JsonSerializer.Deserialize<SourceHolder>(json, JsonOptions);
        back!.SourceType.Should().Be(value);
    }

    [Fact]
    public void OutboundMessageSource_NumericString_Rejected()
    {
        var act = () => JsonSerializer.Deserialize<SourceHolder>("{\"SourceType\":\"1\"}", JsonOptions);
        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(PendingQuestionStatus.Pending, "Pending")]
    [InlineData(PendingQuestionStatus.Answered, "Answered")]
    [InlineData(PendingQuestionStatus.AwaitingComment, "AwaitingComment")]
    [InlineData(PendingQuestionStatus.TimedOut, "TimedOut")]
    public void PendingQuestionStatus_RoundTripsAsName(PendingQuestionStatus value, string name)
    {
        var json = JsonSerializer.Serialize(new QuestionStatusHolder(value), JsonOptions);
        json.Should().Contain($"\"Status\":\"{name}\"");
        var back = JsonSerializer.Deserialize<QuestionStatusHolder>(json, JsonOptions);
        back!.Status.Should().Be(value);
    }

    [Fact]
    public void PendingQuestionStatus_CaseMismatch_Rejected()
    {
        var act = () => JsonSerializer.Deserialize<QuestionStatusHolder>(
            "{\"Status\":\"answered\"}", JsonOptions);
        act.Should().Throw<JsonException>();
    }
}
