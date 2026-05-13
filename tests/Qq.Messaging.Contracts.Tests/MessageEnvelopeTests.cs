using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

public class MessageEnvelopeTests
{
    [Fact]
    public void NewEnvelope_DefaultsToQueuedStatus()
    {
        var envelope = new MessageEnvelope
        {
            EnvelopeId = "env-1",
            DeduplicationKey = "dedup-1",
            Payload = new OutboundMessage(
                "msg-1", "op-1", "Hello", null,
                new CorrelationContext(), MessageSeverity.Normal,
                DateTimeOffset.UtcNow),
            Correlation = new CorrelationContext()
        };

        Assert.Equal(DeliveryStatus.Queued, envelope.Status);
        Assert.Equal(0, envelope.AttemptCount);
    }

    [Fact]
    public void Envelope_PreservesDeduplicationKey()
    {
        var envelope = new MessageEnvelope
        {
            EnvelopeId = "env-2",
            DeduplicationKey = "agent-42:question-99",
            Payload = new OutboundMessage(
                "msg-2", "op-1", "Alert", null,
                new CorrelationContext(), MessageSeverity.Critical,
                DateTimeOffset.UtcNow),
            Correlation = new CorrelationContext()
        };

        Assert.Equal("agent-42:question-99", envelope.DeduplicationKey);
    }
}
