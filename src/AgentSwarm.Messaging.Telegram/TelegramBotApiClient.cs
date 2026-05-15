using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Production <see cref="ITelegramApiClient"/> backed by the
/// <c>Telegram.Bot</c> library's <c>SendMessage</c> extension method.
/// </summary>
internal sealed class TelegramBotApiClient : ITelegramApiClient
{
    private readonly ITelegramBotClient _bot;

    public TelegramBotApiClient(ITelegramBotClient bot)
    {
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
    }

    public async Task<long> SendMessageAsync(
        long chatId,
        string text,
        ParseMode parseMode,
        ReplyMarkup? replyMarkup,
        CancellationToken ct)
    {
        var message = await _bot.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: parseMode,
            replyMarkup: replyMarkup,
            cancellationToken: ct).ConfigureAwait(false);
        return message.MessageId;
    }
}
