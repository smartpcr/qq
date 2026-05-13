namespace Qq.Messaging.Abstractions;

/// <summary>
/// A human operator's typed response to an <see cref="AgentQuestion"/>.
/// </summary>
public sealed record HumanResponse(
    string ResponseId,
    string QuestionId,
    OperatorIdentity Operator,
    string SelectedValue,
    string? RawText,
    DateTimeOffset RespondedAtUtc,
    CorrelationContext Correlation);
