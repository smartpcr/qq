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
///   <see cref="Message.From"/> and a non-empty <see cref="Message.Text"/>
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
///   non-null/whitespace <see cref="CallbackQuery.Data"/> AND a non-null
///   <see cref="CallbackQuery.Message"/> maps to
///   <see cref="EventType.CallbackResponse"/>; the inline-button
///   callback data flows through <see cref="MessengerEvent.Payload"/>.
///   Game-button callbacks (<c>callback_game</c>, where <c>Data</c> is
///   null and <c>GameShortName</c> is set instead) and orphaned
///   inline-mode callbacks (where only <c>InlineMessageId</c> is set
///   and <c>Message</c> is null because the originating message is too
///   old or was sent via inline mode) fall through to the Unknown
///   branch — the approval handler expects a non-null callback payload
///   to parse <c>QuestionId+ActionId</c> out of, and we only ever send
///   approval cards as regular bot messages so an orphaned inline
///   callback cannot belong to any approval flow we initiated.
///   </description></item>
///   <item><description>Anything else — edited messages, channel posts,
///   chat member updates, polls, anonymous admin posts and channel
///   forward headers (a <see cref="Message"/> with a null
///   <see cref="Message.From"/>), etc. — maps to
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

        // Require msg.From to be non-null. Telegram delivers Messages with
        // a null From for anonymous group-admin posts, channel forward
        // headers, and other system-origin payloads that carry no user
        // identity. Synthesizing UserId="0" for those would (a) classify
        // the event as Command/TextReply when authorization has nothing
        // to authorize against, and (b) consume a dedup reservation slot
        // for an event the pipeline should short-circuit. Falling through
        // to the Unknown branch matches the polling-stage sibling mapper
        // (AgentSwarm.Messaging.Telegram.TelegramUpdateMapper), which
        // also requires a non-null From before emitting a typed event.
        if (update.Message is { } msg && msg.From != null && !string.IsNullOrEmpty(msg.Text))
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

        // Require cb.Data to be non-null/whitespace AND cb.Message to be
        // non-null before emitting a typed CallbackResponse. Telegram
        // delivers callback queries with a null Data for game-button
        // callbacks (callback_game carries GameShortName instead of Data)
        // and with a null Message when the originating message was sent
        // via inline mode or is too old (only InlineMessageId is set in
        // those cases). Synthesizing Payload=null would NRE the approval
        // handler, which expects a non-null payload to parse QuestionId
        // and ActionId out of, and synthesizing ChatId="0" for an
        // orphaned inline callback would misroute the event to a chat
        // we never sent an approval card to. Falling through to the
        // Unknown branch matches the polling-stage sibling mapper and
        // the previous webhook mapper's behaviour, and lets the
        // pipeline's classify stage short-circuit before any reservation
        // slot is consumed.
        if (update.CallbackQuery is { } cb
            && !string.IsNullOrWhiteSpace(cb.Data)
            && cb.Message is { } cbMsg)
        {
            return new MessengerEvent
            {
                EventId = eventId,
                EventType = EventType.CallbackResponse,
                RawCommand = null,
                UserId = cb.From.Id.ToString(CultureInfo.InvariantCulture),
                ChatId = cbMsg.Chat.Id.ToString(CultureInfo.InvariantCulture),
                Timestamp = receivedAt,
                CorrelationId = correlationId,
                Payload = cb.Data,
                // Stage 3.3 — preserve the Telegram callback-query id so
                // CallbackQueryHandler can echo it back via
                // AnswerCallbackQueryAsync to stop the spinner on the
                // operator's device, AND use it as the per-callback
                // idempotency key required by implementation-plan.md
                // Stage 3.3 ("if the same CallbackQuery.Id has already
                // been processed, skip processing and re-answer").
                CallbackId = cb.Id,
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
