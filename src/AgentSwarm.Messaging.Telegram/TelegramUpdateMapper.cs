using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Telegram.Bot.Types;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Shared mapper between an inbound Telegram <see cref="Update"/> (raw SDK
/// type) and the transport-agnostic <see cref="MessengerEvent"/> the
/// inbound pipeline (<see cref="ITelegramUpdatePipeline"/>) consumes.
/// </summary>
/// <remarks>
/// <para>
/// Used by both Stage 2.5's <c>TelegramPollingService</c> and the Stage 2.4
/// webhook controller so the <see cref="MessengerEvent.EventId"/> shape is
/// identical across receivers — the deduplication gate must short-circuit
/// re-deliveries that arrive via different transports.
/// </para>
/// <para>
/// <b>EventId.</b> <c>tg-update-{update.Id}</c>. Telegram's
/// <c>update_id</c> is monotonic and unique per bot so it is the natural
/// dedup key. The <c>tg-update-</c> prefix namespaces the value so future
/// connectors (Discord, Slack, Teams) cannot collide on the bare integer.
/// </para>
/// <para>
/// <b>Correlation id.</b> Generated per call via <see cref="Guid.NewGuid"/>.
/// Polling has no inbound trace header to pass through; the operator-facing
/// observability stack (Stage 6.1) starts the trace at the polling boundary.
/// </para>
/// <para>
/// <b>Strictness on required fields.</b> The pipeline's
/// <see cref="MessengerEvent"/> requires non-empty
/// <see cref="MessengerEvent.ChatId"/> and
/// <see cref="MessengerEvent.UserId"/> for authorization to function; when
/// the SDK delivers a typed update with missing <c>From</c>/<c>Chat</c>
/// (anonymous admin posts, channel posts without a sender, etc.) the
/// mapper falls back to <see cref="EventType.Unknown"/> rather than emit a
/// typed event with synthetic identifiers — the pipeline short-circuits
/// <see cref="EventType.Unknown"/> BEFORE authz so misclassified events
/// cannot accidentally clear authorization.
/// </para>
/// </remarks>
public static class TelegramUpdateMapper
{
    /// <summary>
    /// Maps a Telegram <see cref="Update"/> to a
    /// <see cref="MessengerEvent"/>. Never returns <c>null</c>: an
    /// unrecognized or malformed update is surfaced as
    /// <see cref="EventType.Unknown"/> so the pipeline can log + dedup +
    /// short-circuit it.
    /// </summary>
    /// <param name="update">The Telegram update to map.</param>
    /// <returns>A non-null <see cref="MessengerEvent"/>.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="update"/> is null.</exception>
    public static MessengerEvent Map(Update update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var eventId = "tg-update-" + update.Id.ToString(CultureInfo.InvariantCulture);
        var correlationId = Guid.NewGuid().ToString();

        var message = update.Message;
        if (message is not null && message.From is not null && message.Chat is not null && !string.IsNullOrEmpty(message.Text))
        {
            var text = message.Text;
            var chatId = message.Chat.Id.ToString(CultureInfo.InvariantCulture);
            var userId = message.From.Id.ToString(CultureInfo.InvariantCulture);
            var timestamp = ToUtc(message.Date);

            if (text.StartsWith('/'))
            {
                return new MessengerEvent
                {
                    EventId = eventId,
                    EventType = EventType.Command,
                    RawCommand = text,
                    UserId = userId,
                    ChatId = chatId,
                    Timestamp = timestamp,
                    CorrelationId = correlationId,
                    Payload = text,
                };
            }

            return new MessengerEvent
            {
                EventId = eventId,
                EventType = EventType.TextReply,
                UserId = userId,
                ChatId = chatId,
                Timestamp = timestamp,
                CorrelationId = correlationId,
                Payload = text,
            };
        }

        var callback = update.CallbackQuery;
        if (callback is not null
            && callback.From is not null
            && callback.Message?.Chat is not null
            && !string.IsNullOrWhiteSpace(callback.Data))
        {
            return new MessengerEvent
            {
                EventId = eventId,
                EventType = EventType.CallbackResponse,
                UserId = callback.From.Id.ToString(CultureInfo.InvariantCulture),
                ChatId = callback.Message.Chat.Id.ToString(CultureInfo.InvariantCulture),
                // CallbackQuery has no native timestamp — using UtcNow stamps
                // the click time rather than the (potentially stale) age of
                // the original message the button was attached to.
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                Payload = callback.Data,
            };
        }

        // Anything else (edited messages, channel posts, member updates,
        // polls, a typed update with missing From/Chat, OR a CallbackQuery
        // with null/whitespace Data — non-action callbacks such as bare
        // dismissals, game callbacks, or malformed deliveries) maps to
        // Unknown so the pipeline short-circuits before consuming a dedup
        // reservation or running authz on a synthetic identifier. The
        // approval/rejection contract (Stage 3.x) is built on a non-empty
        // callback_data payload; allowing null Data through as a typed
        // CallbackResponse would let a malformed callback reach the
        // ICallbackHandler approval/reject paths with no payload to
        // disambiguate.
        return new MessengerEvent
        {
            EventId = eventId,
            EventType = EventType.Unknown,
            UserId = "unknown",
            ChatId = "unknown",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
        };
    }

    private static DateTimeOffset ToUtc(DateTime date)
    {
        // Telegram.Bot deserialises Message.Date as UTC DateTime with Kind=Utc;
        // SpecifyKind here is a defensive guard so a Kind=Unspecified value
        // (older SDK versions, hand-constructed fakes) does not become a local
        // DateTimeOffset.
        var utc = date.Kind == DateTimeKind.Utc ? date : DateTime.SpecifyKind(date, DateTimeKind.Utc);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
