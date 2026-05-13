namespace AgentSwarm.Messaging.Core.Tests;

/// <summary>
/// Guards the canonical lifecycle states and payload-type discriminators for
/// <see cref="OutboxEntry"/>. Aligned with <c>architecture.md</c> §3.2.
/// </summary>
public sealed class OutboxVocabularyTests
{
    [Fact]
    public void OutboxEntryStatuses_All_ContainsExactlyTheFiveCanonicalStates()
    {
        var expected = new[]
        {
            OutboxEntryStatuses.Pending,
            OutboxEntryStatuses.Processing,
            OutboxEntryStatuses.Sent,
            OutboxEntryStatuses.Failed,
            OutboxEntryStatuses.DeadLettered,
        };

        Assert.Equal(expected, OutboxEntryStatuses.All);
    }

    [Theory]
    [InlineData("Pending", true)]
    [InlineData("Processing", true)]
    [InlineData("Sent", true)]
    [InlineData("Failed", true)]
    [InlineData("DeadLettered", true)]
    [InlineData("Bogus", false)]
    [InlineData("", false)]
    public void OutboxEntryStatuses_IsValid_AcceptsCanonicalAndRejectsOthers(
        string value,
        bool expected)
    {
        Assert.Equal(expected, OutboxEntryStatuses.IsValid(value));
    }

    [Fact]
    public void OutboxEntryStatuses_IsValid_RejectsNull()
    {
        Assert.False(OutboxEntryStatuses.IsValid(null));
    }

    [Fact]
    public void OutboxPayloadTypes_All_ContainsExactlyTheTwoCanonicalPayloads()
    {
        var expected = new[]
        {
            OutboxPayloadTypes.MessengerMessage,
            OutboxPayloadTypes.AgentQuestion,
        };

        Assert.Equal(expected, OutboxPayloadTypes.All);
    }

    [Fact]
    public void OutboxEntry_DefaultStatus_IsPending()
    {
        var entry = new OutboxEntry
        {
            OutboxEntryId = "id",
            CorrelationId = "corr",
            Destination = "teams://t/user/u",
            PayloadType = OutboxPayloadTypes.MessengerMessage,
            PayloadJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal(OutboxEntryStatuses.Pending, entry.Status);
    }
}
