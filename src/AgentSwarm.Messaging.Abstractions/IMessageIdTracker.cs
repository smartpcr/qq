namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Records the mapping from a successfully-sent Telegram message id back
/// to its originating <c>CorrelationId</c> so a follow-up text reply (the
/// operator's comment after a <c>RequiresComment</c> button tap, for
/// example) can be threaded into the originating trace context.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compound key.</b> Telegram <c>message_id</c> values are <i>only</i>
/// unique within a single chat — the same numeric id can be assigned to
/// distinct messages in different chats. The mapping must therefore be
/// keyed by the (<paramref name="chatId"/>, <c>telegramMessageId</c>)
/// pair to prevent cross-chat collisions that would mis-correlate a
/// follow-up reply.
/// </para>
/// <para>
/// <b>Async surface.</b> Both methods are <see cref="Task"/>-returning so
/// the durable EF Core-backed implementation in
/// <c>AgentSwarm.Messaging.Persistence</c> can avoid blocking the calling
/// thread. The in-memory implementation in
/// <c>AgentSwarm.Messaging.Telegram</c> still completes synchronously
/// behind a <see cref="Task.CompletedTask"/>.
/// </para>
/// <para>
/// <b>Layering.</b> The interface lives in
/// <c>AgentSwarm.Messaging.Abstractions</c> so the
/// <c>Persistence</c> project can implement it without referencing the
/// Telegram project. The Telegram <see cref="System.Threading.Tasks.Task"/>
/// sender depends on this abstraction, so swapping in-memory →
/// EF Core → Redis is a DI-only change.
/// </para>
/// <para>
/// <b>Failure semantics — IMPORTANT contract.</b> <see cref="TrackAsync"/>
/// is declared <i>best-effort</i> by the interface itself: implementations
/// MUST NOT propagate persistence failures (database outages, transient
/// I/O errors, lock-conflict, etc.) as exceptions to the caller. The
/// only exception type implementations are permitted to throw is
/// <see cref="System.OperationCanceledException"/> when the supplied
/// <see cref="System.Threading.CancellationToken"/> is signalled.
/// Implementations are expected to perform a small number of bounded
/// inline retries internally (e.g., 3 attempts with exponential backoff)
/// and then log + suppress on persistent failure.
/// </para>
/// <para>
/// <b>Why best-effort, not strict.</b> The Telegram message has already
/// been delivered to the operator's chat by the time
/// <see cref="TrackAsync"/> is invoked. Propagating a persistence
/// failure to the sender would force the upstream
/// <c>OutboundQueueProcessor</c> to retry the send, producing a
/// duplicate operator-visible message. The canonical durable record of
/// every send is the <c>OutboundMessage</c> row written by Stage 4.1's
/// <c>IOutboundQueue.MarkSentAsync</c> (which carries both
/// <c>CorrelationId</c> and <c>TelegramMessageId</c> columns); the
/// <see cref="IMessageIdTracker"/> table is a supplementary
/// fast-lookup index. A future Stage 5.x reply-correlator may reconcile
/// the index by backfilling from <c>OutboundMessage</c> when a tracker
/// lookup misses.
/// </para>
/// </remarks>
public interface IMessageIdTracker
{
    /// <summary>
    /// Persist the mapping. The pair (<paramref name="chatId"/>,
    /// <paramref name="telegramMessageId"/>) is the composite key;
    /// implementations overwrite an existing entry for the same pair
    /// (last-writer-wins) since Telegram never re-uses a
    /// <c>message_id</c> within a single chat.
    /// </summary>
    /// <param name="chatId">Telegram chat id (int64 on the wire).</param>
    /// <param name="telegramMessageId">Telegram-assigned <c>message_id</c>.</param>
    /// <param name="correlationId">Trace identifier; non-null, non-empty,
    /// non-whitespace per the "All messages include trace/correlation ID"
    /// acceptance criterion.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <remarks>
    /// Per the interface-level "Failure semantics" contract, this method
    /// MUST NOT propagate persistence failures. Implementations log and
    /// suppress; only <see cref="System.OperationCanceledException"/>
    /// from <paramref name="ct"/> is permitted to escape.
    /// </remarks>
    Task TrackAsync(
        long chatId,
        long telegramMessageId,
        string correlationId,
        CancellationToken ct);

    /// <summary>
    /// Resolve the previously tracked <c>CorrelationId</c> for the given
    /// (<paramref name="chatId"/>, <paramref name="telegramMessageId"/>)
    /// pair, or <c>null</c> if no mapping was recorded.
    /// </summary>
    /// <param name="chatId">Telegram chat id (int64 on the wire).</param>
    /// <param name="telegramMessageId">Telegram-assigned <c>message_id</c>.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    Task<string?> TryGetCorrelationIdAsync(
        long chatId,
        long telegramMessageId,
        CancellationToken ct);
}
