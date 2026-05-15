using System.Threading;
using System.Threading.Tasks;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Proactive dual-bucket rate limiter contract per architecture.md §10.4.
/// <see cref="TelegramMessageSender"/> awaits a token before each Telegram
/// API call so that the global Bot API ceiling
/// (<see cref="RateLimitOptions.GlobalPerSecond"/>) and the per-chat
/// ceiling (<see cref="RateLimitOptions.PerChatPerMinute"/>) are honoured
/// without ever issuing a request that would be 429'd.
/// </summary>
public interface ITelegramRateLimiter
{
    /// <summary>
    /// Block-asynchronously until one token is available in both the global
    /// bucket and the per-<paramref name="chatId"/> bucket; then consume one
    /// token from each and return. When tokens are exhausted the call awaits
    /// via the injected <see cref="IDelayProvider"/> rather than spinning.
    /// </summary>
    Task AcquireAsync(long chatId, CancellationToken ct);
}
