using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts;

/// <summary>
/// Telegram-specific messenger connector that extends the platform-agnostic
/// <see cref="IMessengerConnector"/> with webhook lifecycle management.
/// </summary>
public interface ITelegramConnector : IMessengerConnector
{
    /// <summary>Register the webhook URL with Telegram.</summary>
    Task SetWebhookAsync(CancellationToken cancellationToken = default);

    /// <summary>Remove the current webhook registration.</summary>
    Task DeleteWebhookAsync(CancellationToken cancellationToken = default);
}
