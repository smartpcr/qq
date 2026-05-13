namespace Qq.Messaging.Abstractions;

/// <summary>
/// A parsed slash-command received from a human operator.
/// </summary>
public sealed record InboundCommand(
    CommandType CommandType,
    string RawText,
    string[] Arguments,
    PlatformPrincipal Principal,
    OperatorIdentity? Operator,
    CorrelationContext Correlation,
    DateTimeOffset ReceivedAtUtc);
