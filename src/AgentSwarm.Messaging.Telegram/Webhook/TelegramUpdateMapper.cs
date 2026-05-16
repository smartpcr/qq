using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Telegram.Bot.Types;

namespace AgentSwarm.Messaging.Telegram.Webhook;

/// <summary>
/// Maps a raw Telegram <see cref="Update"/> to the transport-agnostic
/// <see cref="MessengerEvent"/> consumed by
/// <see cref="ITelegramUpdatePipeline.ProcessAsync"/>. Used by BOTH the
/// webhook controller (Stage 2.4) and the polling service (Stage 2.5)
/// so the downstream pipeline never sees a Telegram-specific shape
/// (architecture.md §4.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>EventType classification.</b>
/// <list type="bullet">
///   <item><description><see cref="Update.Message"/> with a non-empty
///   <see cref="Message.Text"/> that starts with <c>/</c> maps to
///   <see cref="EventType.Command"/>; <see cref="MessengerEvent.RawCommand"/>
///   carries the verbatim text (slash included).</description></item>
///   <item><description><see cref="Update.Message"/> with non-slash text
///   maps to <see cref="EventType.TextReply"/>; the text flows through
///   <see cref="MessengerEvent.Payload"/> so the text-reply handler can
///   read it (the pipeline reads <c>Payload</c> rather than
///   <c>RawCommand</c> for non-command events).</description></item>
///   <item><description><see cref="Update.CallbackQuery"/> maps to
///   <see cref="EventType.CallbackResponse"/>; the inline-button
///   callback data flows through <see cref="MessengerEvent.Payload"/>.
///   </description></item>
///   <item><description>Anything else — edited messages, channel posts,
///   chat member updates, polls, etc. — maps to
///   <see cref="EventType.Unknown"/>. The pipeline's classify stage
///   short-circuits Unknown events BEFORE the dedup gate so no
///   reservation slot is consumed.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Identifier stability.</b> <see cref="MessengerEvent.EventId"/> is
/// <c>tg-update-&lt;update_id&gt;</c> — a stable string keyed by
/// Telegram's monotonic <c>update_id</c>. Two webhook deliveries of
/// the same update share the same EventId, so the pipeline's
/// <see cref="IDeduplicationService.TryReserveAsync"/> gate collapses
/// them to a single execution even if the controller's UNIQUE
/// constraint somehow lets a duplicate through (defense in depth).
/// </para>
/// </remarks>
public static class TelegramUpdateMapper
{
    /// <summary>
    /// Translates <paramref name="update"/> into a transport-agnostic
    /// <see cref="MessengerEvent"/> bound to
    /// <paramref name="correlationId"/>.
    /// </summary>
    public static MessengerEvent Map(Update update, string correlationId, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException(
                "correlationId must be non-null and non-whitespace.", nameof(correlationId));
        }

        var eventId = "tg-update-" + update.Id.ToString(CultureInfo.InvariantCulture);

        if (update.Message is { } msg && !string.IsNullOrEmpty(msg.Text))
        {
            var isCommand = msg.Text.StartsWith('/');
            return new MessengerEvent
            {
                EventId = eventId,
                EventType = isCommand ? EventType.Command : EventType.TextReply,
                RawCommand = isCommand ? msg.Text : null,
                UserId = (msg.From?.Id ?? 0L).ToString(CultureInfo.InvariantCulture),
                ChatId = msg.Chat.Id.ToString(CultureInfo.InvariantCulture),
                Timestamp = receivedAt,
                CorrelationId = correlationId,
                Payload = isCommand ? null : msg.Text,
            };
        }

        if (update.CallbackQuery is { } cb)
        {
            return new MessengerEvent
            {
                EventId = eventId,
                EventType = EventType.CallbackResponse,
                RawCommand = null,
                UserId = cb.From.Id.ToString(CultureInfo.InvariantCulture),
                ChatId = (cb.Message?.Chat.Id ?? 0L).ToString(CultureInfo.InvariantCulture),
                Timestamp = receivedAt,
                CorrelationId = correlationId,
                Payload = cb.Data,
            };
        }

        var fallbackChatId = update.Message?.Chat.Id
            ?? update.CallbackQuery?.Message?.Chat.Id
            ?? update.EditedMessage?.Chat.Id
            ?? update.ChannelPost?.Chat.Id
            ?? 0L;
        var fallbackUserId = update.Message?.From?.Id
            ?? update.CallbackQuery?.From.Id
            ?? update.EditedMessage?.From?.Id
            ?? 0L;
        return new MessengerEvent
        {
            EventId = eventId,
            EventType = EventType.Unknown,
            RawCommand = null,
            UserId = fallbackUserId.ToString(CultureInfo.InvariantCulture),
            ChatId = fallbackChatId.ToString(CultureInfo.InvariantCulture),
            Timestamp = receivedAt,
            CorrelationId = correlationId,
        };
    }
}
