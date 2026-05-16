using System.Threading;
using System.Threading.Tasks;

namespace AgentSwarm.Messaging.Telegram.Sending;

/// <summary>
/// Dual-layer outbound rate limiter for the Telegram Bot API per
/// architecture.md §10.4 — a global limiter capping
/// <see cref="RateLimitOptions.GlobalPerSecond"/> sends per second across
/// all chats and a per-chat limiter capping
/// <see cref="RateLimitOptions.PerChatPerMinute"/> sends per chat per
/// minute. Callers acquire both tokens before issuing a Telegram API
/// call so a worker proactively waits rather than triggering an HTTP
/// 429 from the Bot API.
/// </summary>
public interface ITelegramRateLimiter
{
    /// <summary>
    /// Acquire one token from both layers (global + per-chat) for the
    /// given <paramref name="chatId"/>. Awaits asynchronously when
    /// either bucket is empty; cancellation surfaces as
    /// <see cref="System.OperationCanceledException"/> WITHOUT
    /// consuming a token.
    /// </summary>
    Task AcquireAsync(long chatId, CancellationToken cancellationToken);
}
