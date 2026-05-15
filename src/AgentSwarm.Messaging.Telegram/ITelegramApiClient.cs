using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Narrow Telegram send surface used by <see cref="TelegramMessageSender"/>.
/// Wrapping the static <c>SendMessage</c> extension on
/// <see cref="Telegram.Bot.ITelegramBotClient"/> behind this interface
/// makes the sender unit-testable: Moq can stub
/// <see cref="ITelegramApiClient"/> directly, whereas extension methods on
/// <see cref="Telegram.Bot.ITelegramBotClient"/> cannot be intercepted.
/// </summary>
public interface ITelegramApiClient
{
    /// <summary>
    /// Sends a single text message to <paramref name="chatId"/> and
    /// returns the Telegram-assigned <c>message_id</c>. Implementations
    /// must throw <see cref="Telegram.Bot.Exceptions.ApiRequestException"/>
    /// when the API rejects the request (e.g. HTTP 429) so the caller can
    /// inspect <c>Parameters.RetryAfter</c>.
    /// </summary>
    Task<long> SendMessageAsync(
        long chatId,
        string text,
        ParseMode parseMode,
        ReplyMarkup? replyMarkup,
        CancellationToken ct);
}
