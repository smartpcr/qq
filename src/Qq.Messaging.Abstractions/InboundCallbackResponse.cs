namespace Qq.Messaging.Abstractions;

/// <summary>
/// A button/inline-keyboard callback received from a human operator
/// in response to an <see cref="AgentQuestion"/>.
/// </summary>
public sealed record InboundCallbackResponse(
    string CallbackId,
    string SelectedValue,
    string? QuestionId,
    PlatformPrincipal Principal,
    OperatorIdentity? Operator,
    CorrelationContext Correlation,
    DateTimeOffset ReceivedAtUtc);
