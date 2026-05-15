namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Durable record of the (<see cref="ChatId"/>,
/// <see cref="TelegramMessageId"/>) → <see cref="CorrelationId"/> mapping
/// produced by <c>TelegramMessageSender</c> after a successful Telegram
/// send. Persisted by <c>PersistentMessageIdTracker</c> so a follow-up
/// text reply received in a later process can be threaded back to the
/// originating trace context across restarts.
/// </summary>
/// <remarks>
/// <para>
/// The composite key is intentionally (<see cref="ChatId"/>,
/// <see cref="TelegramMessageId"/>) rather than the message id alone —
/// Telegram <c>message_id</c> values are only unique within a single
/// chat, so a singleton key would mis-correlate cross-chat replies.
/// </para>
/// <para>
/// This entity is the Stage 2.3 supplementary fast-lookup table. The
/// canonical durable record of an outbound send (with full status,
/// attempts, error detail) is the <c>OutboundMessage</c> row written by
/// the Stage 4.1 <c>OutboundQueueProcessor</c> via
/// <c>IOutboundQueue.MarkSentAsync</c>. The two stores agree on
/// (<c>ChatId</c>, <c>TelegramMessageId</c>) → <c>CorrelationId</c> by
/// construction.
/// </para>
/// </remarks>
public sealed class OutboundMessageIdMapping
{
    /// <summary>Telegram chat id (int64 on the wire). Composite-key part 1.</summary>
    public long ChatId { get; set; }

    /// <summary>Telegram-assigned <c>message_id</c>. Composite-key part 2.</summary>
    public long TelegramMessageId { get; set; }

    /// <summary>
    /// Trace identifier — non-null, non-empty, non-whitespace per the
    /// "All messages include trace/correlation ID" acceptance criterion.
    /// Validated by the calling tracker before insert.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>UTC timestamp the mapping was recorded.</summary>
    public DateTimeOffset RecordedAt { get; set; }
}
