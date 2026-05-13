namespace Qq.Messaging.Abstractions;

/// <summary>
/// Platform-agnostic connector for sending messages and questions to human operators.
/// </summary>
public interface IMessengerConnector
{
    /// <summary>Start receiving updates (webhook registration or long-polling loop).</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Gracefully stop receiving updates.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Send a plain or rich message to an operator.</summary>
    Task<DeliveryResult> SendMessageAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Send an agent question with inline buttons to an operator.</summary>
    Task<DeliveryResult> SendQuestionAsync(
        AgentQuestion question,
        string recipientOperatorId,
        CancellationToken cancellationToken = default);
}
