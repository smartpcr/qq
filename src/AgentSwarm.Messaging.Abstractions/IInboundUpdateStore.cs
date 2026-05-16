namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Durable store for inbound Telegram webhook updates. Owns the
/// four-status lifecycle (<see cref="IdempotencyStatus.Received"/> →
/// <see cref="IdempotencyStatus.Processing"/> →
/// <see cref="IdempotencyStatus.Completed"/> /
/// <see cref="IdempotencyStatus.Failed"/>) and provides the recovery
/// queries used by <c>InboundRecoverySweep</c> (Stage 2.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency contract.</b> <see cref="PersistAsync"/> is the only
/// write that creates a row. The implementation MUST treat
/// duplicate <see cref="InboundUpdate.UpdateId"/> as a returned
/// <c>false</c> rather than throwing — Telegram retries a webhook
/// delivery aggressively and the deduplication property
/// ("Duplicate webhook delivery does not execute the same human command
/// twice") relies on the durable UNIQUE constraint on <c>UpdateId</c>.
/// </para>
/// <para>
/// <b>Hybrid retry contract (Stage 2.2 brief).</b>
/// <see cref="MarkCompletedAsync"/> is invoked for BOTH
/// <see cref="PipelineResult.Succeeded"/>=<c>true</c> AND
/// <see cref="PipelineResult.Succeeded"/>=<c>false</c> outcomes —
/// "return = terminal". The <paramref name="handlerErrorDetail"/>
/// parameter carries the handler's <see cref="CommandResult.ErrorCode"/>
/// /  <see cref="CommandResult.ResponseText"/> for the failure case
/// (non-null) and is <c>null</c> on the success path.
/// <see cref="MarkFailedAsync"/> is reserved for UNCAUGHT exceptions
/// thrown out of the pipeline — "throw = retryable" — and writes to
/// <see cref="InboundUpdate.ErrorDetail"/>, NOT
/// <see cref="InboundUpdate.HandlerErrorDetail"/>.
/// </para>
/// </remarks>
public interface IInboundUpdateStore
{
    /// <summary>
    /// Persists a new <see cref="InboundUpdate"/> row. Returns <c>true</c>
    /// when the row was created, <c>false</c> when a row with the same
    /// <see cref="InboundUpdate.UpdateId"/> already exists (the canonical
    /// duplicate-webhook signal).
    /// </summary>
    Task<bool> PersistAsync(InboundUpdate update, CancellationToken ct);

    /// <summary>
    /// Returns the row with the supplied <paramref name="updateId"/>, or
    /// <c>null</c> when no such row exists. Used by the dispatcher to
    /// re-load a row inside a fresh DI scope between enqueue and
    /// dequeue.
    /// </summary>
    Task<InboundUpdate?> GetByUpdateIdAsync(long updateId, CancellationToken ct);

    /// <summary>
    /// Atomically transitions an existing row from
    /// <see cref="IdempotencyStatus.Received"/> or
    /// <see cref="IdempotencyStatus.Failed"/> to
    /// <see cref="IdempotencyStatus.Processing"/>. Returns <c>true</c>
    /// when this caller won the claim; returns <c>false</c> when the row
    /// is missing, already <see cref="IdempotencyStatus.Processing"/>
    /// (another worker holds it), or already
    /// <see cref="IdempotencyStatus.Completed"/>. The atomic semantics
    /// close the dispatcher-vs-sweep race that would otherwise let two
    /// scopes invoke the pipeline against the same row in parallel.
    /// Implementations MUST use a single conditional UPDATE statement so
    /// the database engine arbitrates the race.
    /// </summary>
    Task<bool> TryMarkProcessingAsync(long updateId, CancellationToken ct);

    /// <summary>
    /// Bulk-transitions every row in <see cref="IdempotencyStatus.Processing"/>
    /// back to <see cref="IdempotencyStatus.Received"/>. Intended to run
    /// exactly once at host startup, BEFORE the dispatcher and the
    /// recovery sweep begin claiming rows, so updates that were mid-flight
    /// during a process crash become eligible for replay again. Safe to
    /// run when the table is empty.
    /// </summary>
    Task<int> ResetInterruptedAsync(CancellationToken ct);

    /// <summary>
    /// Transitions an existing row to
    /// <see cref="IdempotencyStatus.Completed"/>. Writes
    /// <paramref name="handlerErrorDetail"/> to
    /// <see cref="InboundUpdate.HandlerErrorDetail"/> when the routed
    /// handler returned <c>Succeeded=false</c> (terminal failure); pass
    /// <c>null</c> on the success path.
    /// </summary>
    Task MarkCompletedAsync(long updateId, string? handlerErrorDetail, CancellationToken ct);

    /// <summary>
    /// Transitions an existing row to <see cref="IdempotencyStatus.Failed"/>,
    /// increments <see cref="InboundUpdate.AttemptCount"/> by one, and
    /// stores <paramref name="errorDetail"/> in
    /// <see cref="InboundUpdate.ErrorDetail"/>. Reserved for UNCAUGHT
    /// pipeline exceptions; the sweep will replay this row while
    /// <see cref="InboundUpdate.AttemptCount"/>
    /// &lt; <c>InboundRecovery:MaxRetries</c>.
    /// </summary>
    Task MarkFailedAsync(long updateId, string errorDetail, CancellationToken ct);

    /// <summary>
    /// Transitions an existing <see cref="IdempotencyStatus.Processing"/>
    /// row back to <see cref="IdempotencyStatus.Received"/> WITHOUT
    /// incrementing <see cref="InboundUpdate.AttemptCount"/>. Intended
    /// for the dispatcher's cancel-mid-flight path: cancellation is
    /// not a failure (the operator is shutting the host down), so the
    /// row must NOT count toward the retry budget; releasing it
    /// returns it to the queue where the recovery sweep (or the next
    /// dispatcher tick) can drain it on the next process startup.
    /// Returns <c>true</c> on a successful Processing→Received
    /// transition; <c>false</c> when the row is missing or not in
    /// <see cref="IdempotencyStatus.Processing"/> (e.g. another worker
    /// already advanced it to Completed before the cancel-handler ran).
    /// </summary>
    Task<bool> ReleaseProcessingAsync(long updateId, CancellationToken ct);

    /// <summary>
    /// Returns rows whose <see cref="InboundUpdate.IdempotencyStatus"/> is
    /// <see cref="IdempotencyStatus.Received"/>,
    /// <see cref="IdempotencyStatus.Processing"/>, or
    /// <see cref="IdempotencyStatus.Failed"/> AND
    /// <see cref="InboundUpdate.AttemptCount"/> &lt;
    /// <paramref name="maxRetries"/>. <c>Received</c> rows represent
    /// enqueue-failure or release-on-cancel cases; <c>Processing</c>
    /// rows represent crash-recovery cases per architecture.md §4.8
    /// ("Received/Processing records represent crash recovery") — the
    /// recovery startup service (<c>InboundUpdateRecoveryStartup</c>)
    /// already bulk-resets Processing→Received before the dispatcher
    /// or sweep start claiming, so Processing rows surfacing here are
    /// either live (TryMarkProcessing CAS will reject reclaiming them
    /// — safe by construction) or stranded (rare: a release-on-cancel
    /// or crash that happened after recovery-startup ran; sweep will
    /// still iterate them so the metric/alert path sees them).
    /// Records in <see cref="IdempotencyStatus.Failed"/> with
    /// <see cref="InboundUpdate.AttemptCount"/> ≥
    /// <paramref name="maxRetries"/> are permanently failing and surface
    /// via <see cref="GetExhaustedAsync"/> for per-row alerting.
    /// </summary>
    Task<IReadOnlyList<InboundUpdate>> GetRecoverableAsync(int maxRetries, CancellationToken ct);

    /// <summary>
    /// Counts rows that have exhausted their retry budget (i.e.
    /// <see cref="InboundUpdate.IdempotencyStatus"/> is
    /// <see cref="IdempotencyStatus.Failed"/> AND
    /// <see cref="InboundUpdate.AttemptCount"/> ≥
    /// <paramref name="maxRetries"/>). Used by the
    /// <c>inbound_update_exhausted_retries</c> metric.
    /// </summary>
    Task<int> GetExhaustedRetryCountAsync(int maxRetries, CancellationToken ct);

    /// <summary>
    /// Returns up to <paramref name="limit"/> exhausted-retry rows
    /// (<see cref="IdempotencyStatus.Failed"/> with
    /// <see cref="InboundUpdate.AttemptCount"/> ≥
    /// <paramref name="maxRetries"/>) so the recovery sweep can log
    /// each row's <see cref="InboundUpdate.UpdateId"/>,
    /// <see cref="InboundUpdate.AttemptCount"/>, and
    /// <see cref="InboundUpdate.ErrorDetail"/> at <c>Error</c> level
    /// per implementation-plan.md §188 / §201. The <paramref name="limit"/>
    /// is a hard cap (e.g. 50) so a flood of exhausted rows does not
    /// produce an unbounded log line burst from a single sweep tick.
    /// Returns an empty list when no rows are exhausted.
    /// </summary>
    Task<IReadOnlyList<InboundUpdate>> GetExhaustedAsync(int maxRetries, int limit, CancellationToken ct);

    /// <summary>
    /// Atomically resets any <see cref="IdempotencyStatus.Processing"/>
    /// row whose <see cref="InboundUpdate.ProcessingStartedAt"/> is
    /// <c>null</c> (legacy / hand-seeded rows) OR older than
    /// <c>DateTimeOffset.UtcNow - <paramref name="staleness"/></c> back
    /// to <see cref="IdempotencyStatus.Received"/>, leaving
    /// <see cref="InboundUpdate.AttemptCount"/> unchanged. Returns the
    /// number of rows reclaimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Iter-5 evaluator item 3.</b> The one-shot
    /// <see cref="ResetInterruptedAsync"/> at host startup cannot recover
    /// a Processing row that became orphaned AFTER startup (e.g. a
    /// process crash mid-pipeline once the host is up, a
    /// <see cref="ReleaseProcessingAsync"/> call whose exception was
    /// swallowed by the cancel handler). Without this method such a
    /// row would remain in <c>Processing</c> until the NEXT host
    /// restart, defeating the periodic-recovery contract — sweep ticks
    /// surface the row via <see cref="GetRecoverableAsync"/> but the
    /// processor's <see cref="TryMarkProcessingAsync"/> CAS rejects
    /// the claim (Processing is not in the eligible states), so the
    /// pipeline never re-runs.
    /// </para>
    /// <para>
    /// <b>Safety threshold.</b> Callers (the recovery sweep) must pass
    /// a <paramref name="staleness"/> conservatively larger than the
    /// maximum healthy handler duration; the story brief targets a
    /// 2-second P95 send latency, so a default in the
    /// minutes-to-hours range is two-to-three orders of magnitude
    /// above the SLA and gives ample headroom before a legitimately
    /// long-running handler is falsely reclaimed.
    /// </para>
    /// </remarks>
    Task<int> ReclaimStaleProcessingAsync(TimeSpan staleness, CancellationToken ct);
}
