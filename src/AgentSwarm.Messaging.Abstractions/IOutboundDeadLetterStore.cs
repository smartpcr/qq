namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Durable dead-letter ledger for outbound Telegram sends that
/// exhausted the in-sender retry budget. Iter-4 evaluator item 4 —
/// the sender now writes a row here every time it gives up on a
/// chat, so the dead-letter outcome is observable in the database
/// regardless of whether Stage 4.1's outbox-row DLQ path has
/// landed yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-after-write semantics.</b> A successful
/// <see cref="RecordAsync"/> MUST be visible to a subsequent
/// <see cref="GetByCorrelationIdAsync"/> from the same process
/// before the call returns. Implementations that buffer writes
/// must flush before returning to satisfy the sender's "alert,
/// record, throw" contract.
/// </para>
/// <para>
/// <b>Idempotency.</b> Each <see cref="OutboundDeadLetterRecord"/>
/// carries its own <see cref="OutboundDeadLetterRecord.DeadLetterId"/>
/// GUID so a second call with the same record is a no-op rather than
/// a duplicate-key throw. The sender generates a fresh GUID for every
/// dead-letter event so two retries of the same chat / correlation
/// produce two separate rows (the operator audit wants to see both
/// failures, not the latest only).
/// </para>
/// </remarks>
public interface IOutboundDeadLetterStore
{
    /// <summary>
    /// Persist a dead-letter record for one exhausted Telegram send.
    /// </summary>
    Task RecordAsync(OutboundDeadLetterRecord record, CancellationToken ct);

    /// <summary>
    /// Return every dead-letter row recorded under the supplied
    /// <paramref name="correlationId"/>, oldest first. Used by the
    /// operator audit screen / the Stage 4.1 reconcile path to
    /// match sender-side exhaustion against outbox-row DLQ state.
    /// </summary>
    Task<IReadOnlyList<OutboundDeadLetterRecord>> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken ct);
}
