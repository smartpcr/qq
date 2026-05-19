namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Categorisation an <see cref="IOutboxDispatcher"/> assigns to the outcome of a single
/// delivery attempt. The retry engine reads this verbatim to decide whether to re-enqueue
/// for a later retry, dead-letter immediately, or mark the entry sent.
/// </summary>
public enum OutboxDispatchOutcome
{
    /// <summary>Delivery succeeded — engine acknowledges the entry as <c>Sent</c>.</summary>
    Success = 0,

    /// <summary>Delivery failed but the failure is potentially recoverable (HTTP 408,
    /// 425, 429, 5xx, network timeouts, etc.). Engine schedules a retry per
    /// <see cref="OutboxOptions"/>.</summary>
    Transient = 1,

    /// <summary>Delivery failed in a way no number of retries will fix (HTTP 4xx other
    /// than the transient subset, payload validation failure, missing conversation
    /// reference, etc.). Engine dead-letters immediately so the operator can review.</summary>
    Permanent = 2,
}

/// <summary>
/// Result of a single dispatch attempt. The engine pattern-matches on
/// <see cref="Outcome"/> to update the outbox row.
/// </summary>
public sealed record OutboxDispatchResult
{
    /// <summary>Categorisation of the delivery attempt.</summary>
    public required OutboxDispatchOutcome Outcome { get; init; }

    /// <summary>Receipt with Teams identifiers on success; <c>null</c> on failure.</summary>
    public OutboxDeliveryReceipt? Receipt { get; init; }

    /// <summary>Human-readable failure reason persisted to <see cref="OutboxEntry.LastError"/>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Server-supplied minimum delay before the next attempt — populated by HTTP 429
    /// Retry-After. The engine clamps this against the computed backoff so the next
    /// retry is delayed by <c>Max(retryAfter, backoff)</c>.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Stage 6.3 iter-11 evaluator fix item 1 — <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>
    /// snapshot captured by the dispatcher <i>at the exact moment the Bot Connector returns
    /// its HTTP acknowledgement</i> (i.e. immediately after the transport call returns,
    /// BEFORE any post-send durability work such as receipt persistence or card-state save).
    /// The retry engine reads this value to record the
    /// <c>teams.card.delivery.duration_ms</c> histogram as
    /// <c>Stopwatch.GetElapsedTime(dequeueTimestamp, BotConnectorAckTimestamp)</c>, so the
    /// observation reflects the literal §6.3 scenario boundary
    /// "queue pickup → Bot Connector HTTP acknowledgement" — not "queue pickup → post-send
    /// persistence complete".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dispatchers that do not have a meaningful BF/HTTP ack moment (e.g. idempotent
    /// replays that skip the transport call entirely) MAY still set this to a timestamp
    /// captured at the point they consider the entry "delivered" — the engine treats it
    /// as opaque. Dispatchers MAY also leave the field <c>null</c>; the engine falls back
    /// to <c>Stopwatch.GetElapsedTime(dequeueTimestamp)</c> at the post-<c>DispatchAsync</c>
    /// observation site (the legacy behavior).
    /// </para>
    /// </remarks>
    public long? BotConnectorAckTimestamp { get; init; }

    /// <summary>Construct a <see cref="OutboxDispatchOutcome.Success"/> result.</summary>
    public static OutboxDispatchResult Success(OutboxDeliveryReceipt receipt) => new()
    {
        Outcome = OutboxDispatchOutcome.Success,
        Receipt = receipt,
    };

    /// <summary>
    /// Construct a <see cref="OutboxDispatchOutcome.Success"/> result that pins the
    /// histogram observation to the supplied <paramref name="botConnectorAckTimestamp"/>
    /// (a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> snapshot captured at
    /// the BF/transport ack point). Per Stage 6.3 iter-11 evaluator fix item 1, this
    /// keeps the <c>teams.card.delivery.duration_ms</c> observation aligned with the
    /// §6.3 scenario's literal "queue pickup → Bot Connector HTTP acknowledgement"
    /// boundary — post-ack persistence steps (RecordSendReceiptAsync, card-state save,
    /// question conversation-id update) are excluded from the histogram even though
    /// they run before <c>DispatchAsync</c> returns.
    /// </summary>
    public static OutboxDispatchResult Success(OutboxDeliveryReceipt receipt, long botConnectorAckTimestamp) => new()
    {
        Outcome = OutboxDispatchOutcome.Success,
        Receipt = receipt,
        BotConnectorAckTimestamp = botConnectorAckTimestamp,
    };

    /// <summary>Construct a <see cref="OutboxDispatchOutcome.Transient"/> result.</summary>
    public static OutboxDispatchResult Transient(string error, TimeSpan? retryAfter = null) => new()
    {
        Outcome = OutboxDispatchOutcome.Transient,
        Error = error,
        RetryAfter = retryAfter,
    };

    /// <summary>Construct a <see cref="OutboxDispatchOutcome.Permanent"/> result.</summary>
    public static OutboxDispatchResult Permanent(string error) => new()
    {
        Outcome = OutboxDispatchOutcome.Permanent,
        Error = error,
    };
}

/// <summary>
/// Single-shot dispatcher that performs the actual messenger delivery for an
/// <see cref="OutboxEntry"/>. Implementations are messenger-specific (Teams, Slack,
/// Discord). The retry engine does not know about Bot Framework or any other transport —
/// it only orchestrates polling, parallelism, rate limiting, and retry scheduling.
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>
    /// Attempt to deliver the supplied <paramref name="entry"/>.
    /// </summary>
    /// <param name="entry">The dequeued, leased outbox row.</param>
    /// <param name="ct">Cancellation token tied to the engine's lifetime.</param>
    /// <returns>The dispatch outcome classification.</returns>
    Task<OutboxDispatchResult> DispatchAsync(OutboxEntry entry, CancellationToken ct);
}
