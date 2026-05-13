namespace AgentSwarm.Messaging.Abstractions.Tests;

/// <summary>
/// Verifies the <see cref="MessengerEvent"/> hierarchy invariants: each concrete subtype
/// stamps the canonical discriminator value, the <c>with</c> expression on a typed reference
/// preserves that discriminator (because the init setter is protected), and the
/// <see cref="CommandEvent"/> constructor rejects out-of-vocabulary discriminators.
/// </summary>
public sealed class MessengerEventTests
{
    [Fact]
    public void DecisionEvent_HasFixedDiscriminator()
    {
        var decision = new DecisionEvent
        {
            EventId = "evt-1",
            CorrelationId = "corr-1",
            Messenger = "Teams",
            ExternalUserId = "aad-user",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new HumanDecisionEvent(
                QuestionId: "q-1",
                ActionValue: "approve",
                Comment: null,
                Messenger: "Teams",
                ExternalUserId: "aad-user",
                ExternalMessageId: "msg-1",
                ReceivedAt: DateTimeOffset.UtcNow,
                CorrelationId: "corr-1"),
        };

        Assert.Equal(MessengerEventTypes.Decision, decision.EventType);
    }

    [Fact]
    public void TextEvent_HasFixedDiscriminator()
    {
        var text = new TextEvent
        {
            EventId = "evt-2",
            CorrelationId = "corr-2",
            Messenger = "Teams",
            ExternalUserId = "aad-user",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = "hello world",
        };

        Assert.Equal(MessengerEventTypes.Text, text.EventType);
    }

    [Theory]
    [InlineData(MessengerEventTypes.AgentTaskRequest)]
    [InlineData(MessengerEventTypes.Command)]
    [InlineData(MessengerEventTypes.Escalation)]
    [InlineData(MessengerEventTypes.PauseAgent)]
    [InlineData(MessengerEventTypes.ResumeAgent)]
    public void CommandEvent_AcceptsAllCanonicalCommandDiscriminators(string discriminator)
    {
        var command = new CommandEvent(discriminator)
        {
            EventId = "evt-3",
            CorrelationId = "corr-3",
            Messenger = "Teams",
            ExternalUserId = "aad-user",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new ParsedCommand(
                CommandType: "agent ask",
                Payload: "design persistence layer",
                CorrelationId: "corr-3"),
        };

        Assert.Equal(discriminator, command.EventType);
    }

    [Theory]
    [InlineData(MessengerEventTypes.Decision)]
    [InlineData(MessengerEventTypes.Text)]
    [InlineData(MessengerEventTypes.InstallUpdate)]
    [InlineData(MessengerEventTypes.Reaction)]
    [InlineData("Bogus")]
    [InlineData("")]
    public void CommandEvent_RejectsNonCommandDiscriminator(string discriminator)
    {
        Assert.Throws<ArgumentException>(() => InvokeCommandEventCtor(discriminator));
    }

    [Fact]
    public void CommandEvent_RejectsNullDiscriminator()
    {
        Assert.Throws<ArgumentException>(() => InvokeCommandEventCtor(null!));
    }

    private static CommandEvent InvokeCommandEventCtor(string discriminator)
    {
        // The constructor argument validates and throws before the object initializer runs.
        // The initializer block is provided only to satisfy the `required` member contract at
        // the call site; in the throwing tests it is never executed.
        return new CommandEvent(discriminator)
        {
            EventId = "x",
            CorrelationId = "x",
            Messenger = "Teams",
            ExternalUserId = "x",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new ParsedCommand("x", "x", "x"),
        };
    }

    [Fact]
    public void WithExpression_OnDecisionEvent_PreservesDiscriminator()
    {
        var decision = new DecisionEvent
        {
            EventId = "evt-1",
            CorrelationId = "corr-1",
            Messenger = "Teams",
            ExternalUserId = "aad-user",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new HumanDecisionEvent(
                "q", "approve", null, "Teams", "aad-user", "msg", DateTimeOffset.UtcNow, "corr"),
        };

        var copy = decision with { EventId = "evt-1-copy" };

        Assert.Equal(MessengerEventTypes.Decision, copy.EventType);
    }

    [Fact]
    public void Source_DefaultsToNull_ForDirectMessages()
    {
        var text = new TextEvent
        {
            EventId = "evt",
            CorrelationId = "corr",
            Messenger = "Teams",
            ExternalUserId = "aad",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = "hi",
        };

        Assert.Null(text.Source);
    }

    [Fact]
    public void Source_AcceptsAllCanonicalSourceValues()
    {
        foreach (var source in MessengerEventSources.All)
        {
            Assert.True(MessengerEventSources.IsValid(source));
        }

        Assert.True(MessengerEventSources.IsValid(null));
        Assert.False(MessengerEventSources.IsValid("Bogus"));
    }
}
