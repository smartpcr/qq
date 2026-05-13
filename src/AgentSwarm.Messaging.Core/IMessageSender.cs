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
/// responsibility — that belongs to <c>OutboundQueueProcessor</c>'s
/// post-send hook into <c>IPendingQuestionStore.StoreAsync</c>.
/// </para>
/// </remarks>
public interface IMessageSender
{
    /// <summary>
    /// Send a plain-text message to <paramref name="chatId"/>. Returns a
    /// <see cref="SendResult"/> carrying the Telegram-assigned message id
    /// so the caller can persist it on the originating
    /// <c>OutboundMessage</c> row via
    /// <see cref="IOutboundQueue.MarkSentAsync"/>.
    /// </summary>
    Task<SendResult> SendTextAsync(long chatId, string text, CancellationToken ct);

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
