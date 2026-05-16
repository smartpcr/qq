using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Cross-platform contract for the actual platform send operation. Concrete
/// implementations wrap each connector's REST client (Discord.Net for Discord,
/// the Telegram Bot API client, Slack Web API, Bot Framework Connector for
/// Teams) and own message rendering. The outbound queue dispatcher invokes
/// these methods to perform the platform-side delivery for messages it has
/// dequeued. See architecture.md Section 4.9 (interface) and Section 10.4
/// (rate-limit-aware batching).
/// </summary>
/// <remarks>
/// Methods return <see cref="SendResult"/> rather than throwing for
/// platform-rejected sends (rate limits, validation errors): the dispatcher
/// classifies the failure into transient (retry via
/// <see cref="IOutboundQueue.MarkFailedAsync"/>) vs terminal (dead-letter)
/// based on the error string. Network/transport exceptions are still
/// permitted to propagate; the dispatcher catches them and treats them as
/// transient.
/// </remarks>
public interface IMessageSender
{
    /// <summary>
    /// Sends a plain text message to the given channel.
    /// </summary>
    /// <param name="channelId">Platform-native channel id (Discord channel snowflake cast to <see cref="long"/>).</param>
    /// <param name="text">Message body. Implementations may truncate per platform limits.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SendResult> SendTextAsync(long channelId, string text, CancellationToken ct);

    /// <summary>
    /// Sends an <see cref="AgentQuestionEnvelope"/> rendered with the platform's
    /// native interactive components (Discord embed + button row, Slack block
    /// kit, Telegram inline keyboard).
    /// </summary>
    /// <param name="channelId">Platform-native channel id.</param>
    /// <param name="envelope">Question envelope (carries the wrapped question and routing metadata).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SendResult> SendQuestionAsync(long channelId, AgentQuestionEnvelope envelope, CancellationToken ct);

    /// <summary>
    /// Sends a batch of low-priority status updates as a single combined
    /// platform message (e.g. one Discord summary embed listing every agent's
    /// status). Used by the outbound dispatcher to satisfy the rate-limit
    /// budget when more than 50 low-severity messages are pending — see
    /// architecture.md Section 10.4. Critical and High severity messages are
    /// never batched and must use <see cref="SendTextAsync"/> /
    /// <see cref="SendQuestionAsync"/>.
    /// </summary>
    /// <remarks>
    /// All messages in <paramref name="messages"/> must share
    /// <paramref name="channelId"/> (i.e. all carry the same
    /// <see cref="OutboundMessage.ChatId"/>) and all must have
    /// <see cref="OutboundMessage.SourceType"/> equal to
    /// <see cref="OutboundMessageSource.StatusUpdate"/> with
    /// <see cref="OutboundMessage.Severity"/> equal to
    /// <see cref="MessageSeverity.Low"/>. Implementations should validate this
    /// and fail fast (returning <see cref="SendResult.Failed(string)"/>) when
    /// the input is mixed — the dispatcher owns grouping the batch returned by
    /// <see cref="IOutboundQueue.DequeueBatchAsync"/> by channel and source type
    /// before invoking this method.
    /// </remarks>
    /// <param name="channelId">Platform-native channel id.</param>
    /// <param name="messages">The batch to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A single <see cref="SendResult"/> describing the combined send. The
    /// <see cref="SendResult.PlatformMessageId"/> identifies the summary message
    /// the implementation posted.
    /// </returns>
    Task<SendResult> SendBatchAsync(
        long channelId,
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken ct);
}
