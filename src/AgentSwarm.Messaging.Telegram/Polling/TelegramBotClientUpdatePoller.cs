using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentSwarm.Messaging.Telegram.Polling;

/// <summary>
/// Default <see cref="ITelegramUpdatePoller"/> backed by a real
/// <see cref="ITelegramBotClient"/>. The class deliberately confines all
/// direct calls to the Telegram.Bot SDK so the rest of the polling stack is
/// transport-agnostic and easy to unit-test.
/// </summary>
internal sealed class TelegramBotClientUpdatePoller : ITelegramUpdatePoller
{
    /// <summary>
    /// Update types the polling loop subscribes to. Listing the desired
    /// types explicitly (rather than an empty array, which means "all
    /// types except member updates") keeps the bandwidth bounded and makes
    /// the contract auditable.
    /// </summary>
    private static readonly UpdateType[] AllowedUpdateTypes = new[]
    {
        UpdateType.Message,
        UpdateType.CallbackQuery,
    };

    private readonly ITelegramBotClient _client;

    public TelegramBotClientUpdatePoller(ITelegramBotClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public Task<Update[]> GetUpdatesAsync(int? offset, int timeout, CancellationToken cancellationToken)
    {
        return _client.GetUpdates(
            offset: offset,
            limit: null,
            timeout: timeout,
            allowedUpdates: AllowedUpdateTypes,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteWebhookAsync(bool dropPendingUpdates, CancellationToken cancellationToken)
    {
        return _client.DeleteWebhook(
            dropPendingUpdates: dropPendingUpdates,
            cancellationToken: cancellationToken);
    }
}
