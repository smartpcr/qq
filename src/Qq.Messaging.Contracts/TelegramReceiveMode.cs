namespace Qq.Messaging.Contracts;

/// <summary>
/// Determines how the bot receives updates from Telegram.
/// </summary>
public enum TelegramReceiveMode
{
    /// <summary>Telegram pushes updates to a registered HTTPS endpoint.</summary>
    Webhook = 0,

    /// <summary>Bot polls Telegram servers for updates (local/dev only).</summary>
    LongPolling = 1
}
