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
///   <item><description><see cref="Update.Message"/> with a non-null
///   <see cref="Message.From"/> AND a non-empty <see cref="Message.Text"/>
///   that starts with <c>/</c> maps to <see cref="EventType.Command"/>;
///   <see cref="MessengerEvent.RawCommand"/> carries the verbatim text
///   (slash included).</description></item>
///   <item><description><see cref="Update.Message"/> with a non-null
///   <see cref="Message.From"/> and non-slash text maps to
///   <see cref="EventType.TextReply"/>; the text flows through
///   <see cref="MessengerEvent.Payload"/> so the text-reply handler can
///   read it (the pipeline reads <c>Payload</c> rather than
///   <c>RawCommand</c> for non-command events).</description></item>
///   <item><description><see cref="Update.CallbackQuery"/> with a
///   non-empty <see cref="CallbackQuery.Data"/> AND a non-null parent
///   <see cref="CallbackQuery.Message"/> maps to
///   <see cref="EventType.CallbackResponse"/>; the inline-button
///   callback data flows through <see cref="MessengerEvent.Payload"/>.
///   Callbacks missing either field (game callbacks, server-dispatched
///   callbacks from outdated inline keyboards, inline-mode callbacks
///   with only <see cref="CallbackQuery.InlineMessageId"/>) fall
///   through to the <see cref="EventType.Unknown"/> branch so they
///   never reach the approval handler with a null
///   <see cref="MessengerEvent.Payload"/> or a synthetic
///   <c>ChatId = "0"</c>.</description></item>
///   <item><description>Anything else — messages with a null
///   <see cref="Message.From"/> (anonymous admin posts, channel forward
///   headers, automatic forwards), edited messages, channel posts,
///   chat member updates, polls, etc. — maps to
///   <see cref="EventType.Unknown"/>. The pipeline's classify stage
///   short-circuits Unknown events BEFORE the dedup gate so no
///   reservation slot is consumed and no synthetic <c>UserId = "0"</c>
///   reaches the authorization pipeline.</description></item>
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

        // Guard the Command/TextReply branch on a non-null From in
        // addition to non-empty Text. Telegram emits messages with
        // From == null for anonymous group-admin posts, channel forward
        // headers, and automatic forwards — they have no user identity,
        // so promoting them to Command/TextReply would synthesize a
        // UserId = "0" that is semantically wrong (the event isn't from
        // user 0; it's from no user) and would consume a dedup
        // reservation slot for an event the authorization pipeline
        // would only reject anyway. Fall through to the Unknown branch
        // so the pipeline's classify stage short-circuits before the
        // dedup gate.
        if (update.Message is { } msg && msg.From is not null && !string.IsNullOrEmpty(msg.Text))
        {
            var isCommand = msg.Text.StartsWith('/');
            return new MessengerEvent
            {
                EventId = eventId,
                EventType = isCommand ? EventType.Command : EventType.TextReply,
                RawCommand = isCommand ? msg.Text : null,
                UserId = msg.From.Id.ToString(CultureInfo.InvariantCulture),
                ChatId = msg.Chat.Id.ToString(CultureInfo.InvariantCulture),
                Timestamp = receivedAt,
                CorrelationId = correlationId,
                Payload = isCommand ? null : msg.Text,
            };
        }

        // Guard the CallbackResponse branch on BOTH a non-empty Data and
        // a non-null parent Message. Telegram allows callback queries
        // without a `data` payload (game callbacks, server-dispatched
        // callbacks from outdated inline keyboards) and inline-mode
        // callbacks carry only `InlineMessageId` with no parent Message.
        // Promoting either shape to CallbackResponse would surface a
        // null Payload or a synthetic `ChatId = "0"` to the approval
        // handler — fall through to Unknown so the pipeline's classify
        // stage short-circuits before the dedup gate.
        if (update.CallbackQuery is { } cb
            && !string.IsNullOrWhiteSpace(cb.Data)
            && cb.Message is not null)
        {
            return new MessengerEvent
            {
                EventId = eventId,
                EventType = EventType.CallbackResponse,
                RawCommand = null,
                UserId = cb.From.Id.ToString(CultureInfo.InvariantCulture),
                ChatId = cb.Message.Chat.Id.ToString(CultureInfo.InvariantCulture),
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
