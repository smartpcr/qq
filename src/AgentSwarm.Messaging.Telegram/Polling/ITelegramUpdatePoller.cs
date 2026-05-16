using Telegram.Bot.Types;

namespace AgentSwarm.Messaging.Telegram.Polling;

/// <summary>
/// Thin testability seam around Telegram.Bot's <c>GetUpdates</c> extension
/// method. The SDK exposes <c>GetUpdates</c> as a static extension on
/// <see cref="Telegram.Bot.ITelegramBotClient"/> which makes direct mocking
/// awkward (the public surface of <c>ITelegramBotClient</c> is
/// <c>SendRequest{T}</c>, not the typed update fetch); injecting this
/// interface lets <see cref="TelegramPollingService"/> be unit-tested
/// without spinning up an HTTP server.
/// </summary>
internal interface ITelegramUpdatePoller
{
    /// <summary>
    /// Issues a long-poll <c>getUpdates</c> request.
    /// </summary>
    /// <param name="offset">
    /// First update id to return. Pass <c>null</c> on the first call to
    /// drain any pending updates; afterwards pass the last processed id
    /// + 1 to acknowledge prior updates.
    /// </param>
    /// <param name="timeout">Long-poll timeout (seconds).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The array of updates returned by Telegram (possibly empty).</returns>
    Task<Update[]> GetUpdatesAsync(int? offset, int timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort delete of any registered webhook before polling starts.
    /// Telegram returns HTTP 409 from <c>getUpdates</c> while a webhook is
    /// registered server-side; deleting the webhook avoids that failure
    /// when the operator switches from webhook to polling mode without
    /// manually clearing the prior registration.
    /// </summary>
    /// <param name="dropPendingUpdates">
    /// When <c>true</c>, Telegram discards updates that accumulated while
    /// the webhook was active. Defaults to <c>false</c> at the call site to
    /// preserve at-least-once delivery semantics.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteWebhookAsync(bool dropPendingUpdates, CancellationToken cancellationToken);
}
