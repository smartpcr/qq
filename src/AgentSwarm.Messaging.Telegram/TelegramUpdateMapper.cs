using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// Note: <c>update_id</c> is unique <i>per bot</i> — multi-bot deployments
/// sharing a single pipeline must extend the seed with a bot identity
/// (e.g. <c>tg-{botId}-update-{update.Id}</c>) before that limit is
/// reached. The current connector binds one bot per host instance.
/// </para>
/// <para>
/// <b>Correlation id.</b> Derived deterministically from the update
/// identity as <c>uuid-v5(<see cref="CorrelationNamespace"/>,
/// "tg-update-{update.Id}")</c>. FR-004 defines
/// <see cref="MessengerEvent.CorrelationId"/> as the end-to-end trace id;
/// because the polling receiver implements at-least-once recovery by
/// leaving <c>offset</c> unchanged when the pipeline throws (see
/// <c>Polling/TelegramPollingService.cs</c>, the broken-batch path), the
/// same Telegram <c>update_id</c> is re-fetched and re-mapped on every
/// retry. A fresh random id per call would fragment the trace: partial
/// spans emitted before the throw could not be stitched to the retry's
/// spans by CorrelationId alone, only by EventId. Stable-across-retries
/// is therefore the only contract under which observability is coherent
/// for the at-least-once delivery guarantee. The webhook receiver
/// (Stage 2.4) shares this mapper and benefits from the same property —
/// Telegram retries un-acknowledged webhook deliveries with an identical
/// <c>update_id</c>, so each retry mints the same CorrelationId. If
/// per-attempt fan-out is ever needed for telemetry, it belongs on a
/// separate attempt-scoped id minted at the retry boundary, not on
/// CorrelationId.
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
    /// RFC 4122 §4.3 v5 namespace for Telegram-update CorrelationId
    /// derivation. Frozen at the introduction of deterministic
    /// CorrelationId — changing this value would silently re-key every
    /// retry-stable trace and break correlation against any persisted
    /// CorrelationIds in the inbound store / dead-letter queue.
    /// </summary>
    private static readonly Guid CorrelationNamespace =
        new Guid("e1b0d2f5-4a31-4c8b-9d3a-7c5e8f2a6d4c");

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
        var correlationId = DeriveCorrelationId(eventId);

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
                    ChatType = Webhook.TelegramUpdateMapper.FormatChatType(message.Chat.Type),
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
                ChatType = Webhook.TelegramUpdateMapper.FormatChatType(message.Chat.Type),
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
                ChatType = Webhook.TelegramUpdateMapper.FormatChatType(callback.Message.Chat.Type),
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

    private static string DeriveCorrelationId(string eventId)
    {
        // UUID v5 (RFC 4122 §4.3) is the canonical "stable id derived from
        // a name" primitive. Seeding with the EventId guarantees that two
        // Map calls for the same Telegram update.Id produce the same
        // CorrelationId — which is exactly what the at-least-once retry
        // path in TelegramPollingService needs to keep the trace coherent
        // across attempts.
        return CreateUuidV5(CorrelationNamespace, eventId).ToString();
    }

    private static Guid CreateUuidV5(Guid namespaceId, string name)
    {
        // RFC 4122 §4.3: v5 = SHA-1(namespace_bytes || name_bytes) truncated
        // to 128 bits, then overwrite version (top nibble of byte 6) and
        // variant (top two bits of byte 8). The hash input MUST use the
        // namespace bytes in big-endian / network order — otherwise the
        // derived UUID would differ between .NET (mixed-endian Guid
        // layout) and any other RFC-conformant implementation, and we
        // would lose the property that a sibling connector (Discord, the
        // webhook controller, a future Python tool) can re-derive the
        // same CorrelationId from the same EventId.
        var namespaceBytes = namespaceId.ToByteArray();
        SwapToRfcByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(input);

        var guidBytes = new byte[16];
        Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);

        // Version 5 (name-based, SHA-1).
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        // Variant RFC 4122 (10xx).
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        // Swap back to .NET's mixed-endian Guid layout so the resulting
        // Guid's ToString() matches the canonical RFC textual form.
        SwapToRfcByteOrder(guidBytes);
        return new Guid(guidBytes);
    }

    private static void SwapToRfcByteOrder(byte[] guid)
    {
        // .NET's Guid.ToByteArray() emits little-endian for Data1 (bytes
        // 0..3), Data2 (bytes 4..5), and Data3 (bytes 6..7); Data4 (bytes
        // 8..15) is already big-endian. RFC 4122 specifies big-endian
        // throughout. Reversing the three little-endian fields converts
        // both directions (the operation is its own inverse).
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
