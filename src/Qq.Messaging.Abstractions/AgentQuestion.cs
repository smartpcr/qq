namespace Qq.Messaging.Abstractions;

/// <summary>
/// A blocking question from an agent to a human operator.
/// Includes context, severity, timeout, proposed default, and interactive buttons.
/// </summary>
public sealed record AgentQuestion(
    string QuestionId,
    string AgentId,
    string Context,
    MessageSeverity Severity,
    TimeSpan Timeout,
    string ProposedDefaultAction,
    IReadOnlyList<QuestionButton> Buttons,
    CorrelationContext Correlation);
