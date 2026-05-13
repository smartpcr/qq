namespace AgentSwarm.Messaging.Persistence.Tests;

/// <summary>
/// Verifies that <see cref="AuditEntry"/> enforces the canonical vocabulary for
/// <see cref="AuditEntry.EventType"/>, <see cref="AuditEntry.ActorType"/>, and
/// <see cref="AuditEntry.Outcome"/> at construction time — including under <c>with</c>
/// expressions — so callers cannot persist an audit row carrying an off-vocabulary
/// discriminator. Per <c>tech-spec.md</c> §4.3 these three fields define a closed set;
/// off-set values would corrupt compliance filtering and forensic analysis.
/// </summary>
public sealed class AuditEntryVocabularyValidationTests
{
    private static AuditEntry ValidEntry()
    {
        var ts = DateTimeOffset.UtcNow;
        return new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr",
            EventType = AuditEventTypes.CommandReceived,
            ActorId = "actor",
            ActorType = AuditActorTypes.User,
            TenantId = "tenant",
            AgentId = null,
            TaskId = null,
            ConversationId = null,
            Action = "agent ask",
            PayloadJson = "{}",
            Outcome = AuditOutcomes.Success,
            Checksum = AuditEntry.ComputeChecksum(
                ts, "corr", AuditEventTypes.CommandReceived, "actor", AuditActorTypes.User,
                "tenant", null, null, null, "agent ask", "{}", AuditOutcomes.Success),
        };
    }

    [Fact]
    public void Construct_AllCanonicalValues_Succeed()
    {
        foreach (var eventType in AuditEventTypes.All)
        {
            foreach (var actorType in AuditActorTypes.All)
            {
                foreach (var outcome in AuditOutcomes.All)
                {
                    var ts = DateTimeOffset.UtcNow;
                    var entry = new AuditEntry
                    {
                        Timestamp = ts,
                        CorrelationId = "corr",
                        EventType = eventType,
                        ActorId = "actor",
                        ActorType = actorType,
                        TenantId = "tenant",
                        Action = "act",
                        PayloadJson = "{}",
                        Outcome = outcome,
                        Checksum = "n/a",
                    };

                    Assert.Equal(eventType, entry.EventType);
                    Assert.Equal(actorType, entry.ActorType);
                    Assert.Equal(outcome, entry.Outcome);
                }
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bogus")]
    // Domain MessengerEvent.EventType values are intentionally NOT audit event types —
    // confirm the boundary cannot be crossed.
    [InlineData("AgentTaskRequest")]
    [InlineData("Decision")]
    [InlineData("commandreceived")] // case-sensitive vocabulary check
    public void Construct_InvalidEventType_ThrowsArgumentException(string invalidEventType)
    {
        var ts = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<ArgumentException>(() => new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr",
            EventType = invalidEventType,
            ActorId = "actor",
            ActorType = AuditActorTypes.User,
            TenantId = "tenant",
            Action = "act",
            PayloadJson = "{}",
            Outcome = AuditOutcomes.Success,
            Checksum = "n/a",
        });

        Assert.Equal(nameof(AuditEntry.EventType), ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Service")]
    [InlineData("user")] // case-sensitive
    [InlineData("agent")]
    public void Construct_InvalidActorType_ThrowsArgumentException(string invalidActorType)
    {
        var ts = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<ArgumentException>(() => new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr",
            EventType = AuditEventTypes.CommandReceived,
            ActorId = "actor",
            ActorType = invalidActorType,
            TenantId = "tenant",
            Action = "act",
            PayloadJson = "{}",
            Outcome = AuditOutcomes.Success,
            Checksum = "n/a",
        });

        Assert.Equal(nameof(AuditEntry.ActorType), ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("OK")]
    [InlineData("success")] // case-sensitive
    [InlineData("Deadlettered")]
    public void Construct_InvalidOutcome_ThrowsArgumentException(string invalidOutcome)
    {
        var ts = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<ArgumentException>(() => new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr",
            EventType = AuditEventTypes.CommandReceived,
            ActorId = "actor",
            ActorType = AuditActorTypes.User,
            TenantId = "tenant",
            Action = "act",
            PayloadJson = "{}",
            Outcome = invalidOutcome,
            Checksum = "n/a",
        });

        Assert.Equal(nameof(AuditEntry.Outcome), ex.ParamName);
    }

    [Fact]
    public void WithExpression_InvalidEventType_Throws()
    {
        var entry = ValidEntry();
        Assert.Throws<ArgumentException>(() => entry with { EventType = "BogusType" });
    }

    [Fact]
    public void WithExpression_InvalidActorType_Throws()
    {
        var entry = ValidEntry();
        Assert.Throws<ArgumentException>(() => entry with { ActorType = "BogusActor" });
    }

    [Fact]
    public void WithExpression_InvalidOutcome_Throws()
    {
        var entry = ValidEntry();
        Assert.Throws<ArgumentException>(() => entry with { Outcome = "BogusOutcome" });
    }

    [Fact]
    public void WithExpression_ValidVocabularyChange_Succeeds()
    {
        var entry = ValidEntry();

        var derived = entry with
        {
            EventType = AuditEventTypes.MessageActionReceived,
            ActorType = AuditActorTypes.Agent,
            Outcome = AuditOutcomes.Failed,
        };

        Assert.Equal(AuditEventTypes.MessageActionReceived, derived.EventType);
        Assert.Equal(AuditActorTypes.Agent, derived.ActorType);
        Assert.Equal(AuditOutcomes.Failed, derived.Outcome);

        // The original is untouched (record immutability + cloning).
        Assert.Equal(AuditEventTypes.CommandReceived, entry.EventType);
        Assert.Equal(AuditActorTypes.User, entry.ActorType);
        Assert.Equal(AuditOutcomes.Success, entry.Outcome);
    }

    [Fact]
    public void Construct_NullEventType_Throws()
    {
        var ts = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr",
            EventType = null!,
            ActorId = "actor",
            ActorType = AuditActorTypes.User,
            TenantId = "tenant",
            Action = "act",
            PayloadJson = "{}",
            Outcome = AuditOutcomes.Success,
            Checksum = "n/a",
        });
    }

    [Fact]
    public void Construct_NullActorType_Throws()
    {
        var ts = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr",
            EventType = AuditEventTypes.CommandReceived,
            ActorId = "actor",
            ActorType = null!,
            TenantId = "tenant",
            Action = "act",
            PayloadJson = "{}",
            Outcome = AuditOutcomes.Success,
            Checksum = "n/a",
        });
    }

    [Fact]
    public void Construct_NullOutcome_Throws()
    {
        var ts = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr",
            EventType = AuditEventTypes.CommandReceived,
            ActorId = "actor",
            ActorType = AuditActorTypes.User,
            TenantId = "tenant",
            Action = "act",
            PayloadJson = "{}",
            Outcome = null!,
            Checksum = "n/a",
        });
    }
}
