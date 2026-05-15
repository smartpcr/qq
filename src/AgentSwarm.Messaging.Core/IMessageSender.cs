namespace AgentSwarm.Messaging.Core;

using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Return value from <see cref="IMessageSender"/> methods, carrying the
/// Telegram-assigned <c>message_id</c> so the caller (the
/// <c>OutboundQueueProcessor</c> from Stage 4.1) can pass it to both
/// <see cref="IOutboundQueue.MarkSentAsync"/> and
/// <c>IPendingQuestionStore.StoreAsync</c> as post-send hooks.
/// </summary>
/// <remarks>
/// <para>
/// Defined as a <c>sealed</c> positional record per architecture.md §4.12,
/// living in <c>AgentSwarm.Messaging.Core</c> alongside
/// <see cref="IMessageSender"/>.
/// </para>
/// <para>
/// <see cref="TelegramMessageId"/> is typed as <see cref="long"/> per the
/// architecture.md §3.1 canonical-type convention — Telegram chat and
/// message identifiers are <c>int64</c> on the wire and must round-trip
/// without truncation.
/// </para>
/// </remarks>
/// <param name="TelegramMessageId">
/// The Telegram-assigned <c>message_id</c> for the freshly-sent message.
/// </param>
public sealed record SendResult(long TelegramMessageId);

/// <summary>
/// Platform-agnostic outbound sending contract used by the
/// <c>OutboundQueueProcessor</c> (Stage 4.1) to send messages without
/// taking a project reference on <c>AgentSwarm.Messaging.Telegram</c>.
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>AgentSwarm.Messaging.Core</c> per architecture.md §4.12.
/// Stays in Core because <see cref="SendResult"/> is a Core type that is
/// part of the return contract; <see cref="IOutboundQueue"/> and
/// <see cref="OutboundMessage"/> were moved to Abstractions during Stage
/// 1.4 because their brief explicitly placed them there.
/// </para>
/// <para>
/// The concrete <c>TelegramMessageSender</c> (Stage 2.3, in
/// <c>AgentSwarm.Messaging.Telegram</c>) implements this interface and
/// wraps <c>ITelegramBotClient</c> from the <c>Telegram.Bot</c> library.
/// </para>
/// <para>
/// <see cref="SendQuestionAsync"/> takes the full
/// <see cref="AgentQuestionEnvelope"/> rather than a bare
/// <see cref="AgentQuestion"/> so the sender can read
/// <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/> from
/// sidecar metadata and display the proposed default in the rendered
/// message body (e.g. <c>"Default action if no response: Approve"</c>).
/// Per implementation-plan.md Stage 1.4, denormalising
/// <c>PendingQuestionRecord.DefaultActionId</c> is **not** the sender's
/// responsibility — that belongs to the Stage 4.1
/// <c>OutboundQueueProcessor</c>'s post-send hook into the Stage 3.5
/// <c>IPendingQuestionStore.StoreAsync</c>. The Stage 2.3 sender renders
/// the label only.
/// </para>
/// <para>
/// <b>Correlation propagation.</b> The two-arg
/// <see cref="SendTextAsync(long,string,CancellationToken)"/> overload is
/// the legacy entry point for callers that have no correlation context
/// (e.g. simple <c>/start</c> command acks). Production callers
/// (<c>OutboundQueueProcessor</c>) MUST use
/// <see cref="SendMessageAsync(long,MessengerMessage,CancellationToken)"/>,
/// which propagates the <see cref="MessengerMessage.CorrelationId"/>
/// through to the rendered footer and the <c>IMessageIdTracker</c>
/// reply-correlation mapping.
/// </para>
/// </remarks>
public interface IMessageSender
{
    /// <summary>
    /// Send a plain-text message to <paramref name="chatId"/> with no
    /// correlation context. Returns a <see cref="SendResult"/> carrying
    /// the Telegram-assigned message id so the caller can persist it on
    /// the originating <c>OutboundMessage</c> row via
    /// <see cref="IOutboundQueue.MarkSentAsync"/>.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="SendMessageAsync(long,MessengerMessage,CancellationToken)"/>
    /// for any path where a <c>CorrelationId</c> is known — the latter
    /// renders the trace footer and records the
    /// <c>IMessageIdTracker</c> mapping for reply correlation.
    /// </remarks>
    Task<SendResult> SendTextAsync(long chatId, string text, CancellationToken ct);

    /// <summary>
    /// Send a <see cref="MessengerMessage"/> to <paramref name="chatId"/>,
    /// rendering the body with <see cref="MessengerMessage.Text"/> and the
    /// trace footer derived from <see cref="MessengerMessage.CorrelationId"/>
    /// so every chunk of a split message carries the same trace id, and
    /// recording the (chatId, telegramMessageId) → correlationId mapping
    /// in <c>IMessageIdTracker</c> for the follow-up reply path.
    /// </summary>
    /// <remarks>
    /// This is the production-path send method consumed by the Stage 4.1
    /// <c>OutboundQueueProcessor</c>. The two-arg
    /// <see cref="SendTextAsync(long,string,CancellationToken)"/> overload
    /// remains for legacy callers that have no correlation context.
    /// </remarks>
    Task<SendResult> SendMessageAsync(long chatId, MessengerMessage message, CancellationToken ct);

    /// <summary>
    /// Render and send an agent question to <paramref name="chatId"/>.
    /// The <paramref name="envelope"/> carries the question payload plus
    /// sidecar metadata (<see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>,
    /// <see cref="AgentQuestionEnvelope.RoutingMetadata"/>) the sender uses
    /// at render time. Returns a <see cref="SendResult"/> carrying the
    /// Telegram-assigned message id so the caller can both
    /// <see cref="IOutboundQueue.MarkSentAsync"/> the outbound row and
    /// <c>IPendingQuestionStore.StoreAsync</c> the pending-question
    /// record with the correct <c>TelegramMessageId</c>.
    /// </summary>
    Task<SendResult> SendQuestionAsync(long chatId, AgentQuestionEnvelope envelope, CancellationToken ct);
}
