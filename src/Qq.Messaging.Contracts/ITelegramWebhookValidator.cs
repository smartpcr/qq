namespace Qq.Messaging.Contracts;

/// <summary>
/// Validates inbound webhook requests from Telegram
/// using the secret token header.
/// </summary>
public interface ITelegramWebhookValidator
{
    /// <summary>
    /// Validate the X-Telegram-Bot-Api-Secret-Token header value.
    /// Returns true when the token matches the configured secret.
    /// </summary>
    bool Validate(string? secretTokenHeader);
}
