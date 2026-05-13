namespace AgentSwarm.Messaging.Persistence.Tests;

/// <summary>
/// Guards the canonical audit-vocabulary constants from drift away from
/// <c>tech-spec.md</c> §4.3 (Canonical Audit Record Schema).
/// </summary>
public sealed class AuditVocabularyTests
{
    [Fact]
    public void AuditEventTypes_All_ContainsExactlyTheSevenCanonicalValues()
    {
        var expected = new[]
        {
            "CommandReceived",
            "MessageSent",
            "CardActionReceived",
            "SecurityRejection",
            "ProactiveNotification",
            "MessageActionReceived",
            "Error",
        };

        Assert.Equal(expected.Length, AuditEventTypes.All.Count);
        foreach (var value in expected)
        {
            Assert.Contains(value, AuditEventTypes.All);
            Assert.True(AuditEventTypes.IsValid(value));
        }
    }

    [Fact]
    public void AuditEventTypes_IsValid_RejectsUnknownAndNull()
    {
        Assert.False(AuditEventTypes.IsValid(null));
        Assert.False(AuditEventTypes.IsValid(""));
        Assert.False(AuditEventTypes.IsValid("Bogus"));
        // Domain MessengerEvent.EventType values are intentionally NOT audit event types.
        Assert.False(AuditEventTypes.IsValid("AgentTaskRequest"));
        Assert.False(AuditEventTypes.IsValid("Decision"));
    }

    [Fact]
    public void AuditActorTypes_All_ContainsUserAndAgent()
    {
        Assert.Equal(new[] { "User", "Agent" }, AuditActorTypes.All);
        Assert.True(AuditActorTypes.IsValid("User"));
        Assert.True(AuditActorTypes.IsValid("Agent"));
        Assert.False(AuditActorTypes.IsValid(null));
        Assert.False(AuditActorTypes.IsValid("Service"));
    }

    [Fact]
    public void AuditOutcomes_All_ContainsCanonicalFour()
    {
        Assert.Equal(new[] { "Success", "Rejected", "Failed", "DeadLettered" }, AuditOutcomes.All);
        Assert.True(AuditOutcomes.IsValid("Success"));
        Assert.True(AuditOutcomes.IsValid("Rejected"));
        Assert.True(AuditOutcomes.IsValid("Failed"));
        Assert.True(AuditOutcomes.IsValid("DeadLettered"));
        Assert.False(AuditOutcomes.IsValid(null));
        Assert.False(AuditOutcomes.IsValid("OK"));
    }

    [Fact]
    public void MessageDirections_All_ContainsInboundAndOutbound()
    {
        Assert.Equal(new[] { "Inbound", "Outbound" }, MessageDirections.All);
        Assert.True(MessageDirections.IsValid("Inbound"));
        Assert.True(MessageDirections.IsValid("Outbound"));
        Assert.False(MessageDirections.IsValid(null));
        Assert.False(MessageDirections.IsValid("In"));
    }
}
