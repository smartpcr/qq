namespace Qq.Messaging.Contracts;

/// <summary>
/// Configuration for the Telegram Bot connector.
/// Bot token is retrieved at runtime via <see cref="Qq.Messaging.Abstractions.ISecretProvider"/>
/// and is never stored in this settings object.
/// </summary>
public sealed class TelegramBotSettings
{
    /// <summary>Name of the secret holding the bot token.</summary>
    public string BotTokenSecretName { get; set; } = "TelegramBotToken";

    /// <summary>HTTPS URL where Telegram sends webhook updates.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Secret token Telegram includes in the X-Telegram-Bot-Api-Secret-Token header.</summary>
    public string? WebhookSecretToken { get; set; }

    /// <summary>Receive mode: Webhook for production, LongPolling for local development.</summary>
    public TelegramReceiveMode ReceiveMode { get; set; } = TelegramReceiveMode.Webhook;

    /// <summary>Maximum concurrent webhook connections Telegram may open.</summary>
    public int MaxConnections { get; set; } = 40;

    /// <summary>Update types the bot is interested in.</summary>
    public string[] AllowedUpdates { get; set; } = ["message", "callback_query"];

    /// <summary>Maximum retry attempts before dead-lettering an outbound message.</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Base delay between retries (exponential back-off is applied).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}
