namespace AgentSwarm.Messaging.Core;

using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Return value from <see cref="IMessageSender"/> methods, carrying the
/// Telegram-assigned <c>message_id</c> for the freshly-sent message
/// and (iter-2 evaluator item 1) sender-side metadata the Stage 4.1
/// <c>OutboundQueueProcessor</c> needs to decide which canonical
/// latency histograms to emit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caller responsibilities for <see cref="TelegramMessageId"/>
/// (Stage 3.5 contract).</b> The Stage 4.1
/// <c>OutboundQueueProcessor</c> consumes this value for exactly one
/// purpose: it calls <see cref="IOutboundQueue.MarkSentAsync"/> on the
/// originating <c>OutboundMessage</c> row. The processor does
/// <b>not</b> call <c>IPendingQuestionStore.StoreAsync</c> as a
/// post-send hook on the happy path — that call is owned by the
/// concrete <c>TelegramMessageSender</c> and happens
/// <b>inside</b> <c>SendQuestionAsync</c> before this
/// <see cref="SendResult"/> is returned. The only time the processor
/// touches <c>IPendingQuestionStore</c> is the
/// <c>PendingQuestionPersistenceException</c> recovery path
/// (sender already delivered the Telegram message but the durable
/// store call failed; the processor retries ONLY the store call,
/// never re-sends), see architecture.md §5.2 invariant 1.
/// </para>
/// <para>
/// <b>Caller responsibilities for <see cref="RateLimited"/>
/// (Stage 4.1 iter-2 evaluator item 1).</b> The acceptance-gate
/// histogram <c>telegram.send.first_attempt_latency_ms</c> per
/// architecture.md §10.4 is scoped to "first-attempt sends that did
/// NOT receive a Telegram 429". Without a signal from the sender,
/// the processor cannot tell whether a successful send waited
/// internally for a 429 retry-after — and silently including those
/// samples poisons the SLO histogram. The sender sets
/// <see cref="RateLimited"/> = <see langword="true"/> when the
/// underlying <c>SendWithRetry</c> waited on at least one 429
/// retry-after for any chunk of the send; the processor then emits
/// <c>all_attempts_latency_ms</c> only, not
/// <c>first_attempt_latency_ms</c>.
/// </para>
/// <para>
/// Defined as a <c>sealed</c> record per architecture.md §4.12,
/// living in <c>AgentSwarm.Messaging.Core</c> alongside
/// <see cref="IMessageSender"/>. The positional <see cref="TelegramMessageId"/>
/// constructor is preserved for source compatibility; the
/// <see cref="RateLimited"/> flag is exposed as an init-only property
/// so the single-positional-arg ctor at every existing call site
/// continues to compile (<c>new SendResult(123L)</c> stays valid;
/// rate-limit-aware callers use
/// <c>new SendResult(123L) { RateLimited = true }</c>).
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
public sealed record SendResult(long TelegramMessageId)
{
    /// <summary>
    /// <b>Iter-2 evaluator item 1.</b> <see langword="true"/> when
    /// the underlying Bot API call retried internally after at least
    /// one Telegram 429 (flood-control) response before succeeding —
    /// i.e. the elapsed time from enqueue to HTTP 200 included a
    /// <c>retry_after</c> wait. <see langword="false"/> for sends
    /// that returned HTTP 200 on the first wire attempt with no
    /// rate-limit retry. The Stage 4.1
    /// <c>OutboundQueueProcessor</c> uses this flag to satisfy the
    /// architecture.md §10.4 scope rule for
    /// <c>telegram.send.first_attempt_latency_ms</c> (SLO histogram
    /// excludes 429-retried sends) without polluting
    /// <c>telegram.send.all_attempts_latency_ms</c> (capacity-
    /// planning histogram includes them).
    /// </summary>
    public bool RateLimited { get; init; }
}

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
/// </remarks>
public interface IMessageSender
{
    /// <summary>
    /// Send a plain-text message to <paramref name="chatId"/>.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="SendResult.TelegramMessageId"/> is consumed
    /// by the Stage 4.1 <c>OutboundQueueProcessor</c> caller solely for
    /// <see cref="IOutboundQueue.MarkSentAsync"/> on the originating
    /// <c>OutboundMessage</c> row. There is no pending-question record
    /// associated with a plain text send, so
    /// <c>IPendingQuestionStore.StoreAsync</c> is irrelevant for this
    /// method.
    /// </remarks>
    Task<SendResult> SendTextAsync(long chatId, string text, CancellationToken ct);

    /// <summary>
    /// Render and send an agent question to <paramref name="chatId"/>,
    /// then persist the resulting pending-question row inline before
    /// returning. The <paramref name="envelope"/> carries the question
    /// payload plus sidecar metadata
    /// (<see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>,
    /// <see cref="AgentQuestionEnvelope.RoutingMetadata"/>) the sender
    /// uses at render time and at persistence time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Persistence ownership (Stage 3.5).</b> The concrete
    /// <c>TelegramMessageSender</c> calls
    /// <c>IPendingQuestionStore.StoreAsync</c> <b>itself</b>, inside this
    /// method, immediately after the Telegram round-trip succeeds and
    /// before returning the <see cref="SendResult"/>. The caller does
    /// <b>not</b> own a post-send <c>StoreAsync</c> hook on the happy
    /// path.
    /// </para>
    /// <para>
    /// <b>Caller responsibilities for the returned
    /// <see cref="SendResult.TelegramMessageId"/>.</b> The Stage 4.1
    /// <c>OutboundQueueProcessor</c> uses it only to call
    /// <see cref="IOutboundQueue.MarkSentAsync"/> on the originating
    /// <c>OutboundMessage</c> row.
    /// </para>
    /// <para>
    /// <b>Recovery path.</b> If the inline pending-question persistence
    /// fails after a successful Telegram send, this method throws
    /// <c>PendingQuestionPersistenceException</c> carrying
    /// QuestionId / chatId / messageId / correlationId. The processor's
    /// catch handler then retries ONLY
    /// <c>IPendingQuestionStore.StoreAsync</c> using the exception's
    /// recovery context; it must NOT re-issue this <c>SendQuestionAsync</c>
    /// call, since the Telegram message is already delivered and a re-send
    /// would duplicate it. See architecture.md §5.2 invariant 1.
    /// </para>
    /// </remarks>
    Task<SendResult> SendQuestionAsync(long chatId, AgentQuestionEnvelope envelope, CancellationToken ct);
}
