namespace Qq.Messaging.Abstractions;

/// <summary>
/// An outbound text or rich message destined for a human operator.
/// </summary>
public sealed record OutboundMessage(
    string MessageId,
    string RecipientOperatorId,
    string Body,
    IReadOnlyList<QuestionButton>? Buttons,
    CorrelationContext Correlation,
    MessageSeverity Severity,
    DateTimeOffset QueuedAtUtc);
