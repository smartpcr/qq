namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Deduplication and durable work-queue record for inbound Telegram updates.
/// Defined in Abstractions so IInboundUpdateStore can reference it without
/// depending on the Persistence project.
/// </summary>
public sealed record InboundUpdate
{
    /// <summary>Telegram's monotonic update_id. Primary key.</summary>
    public required long UpdateId { get; init; }

    /// <summary>Full serialized Telegram Update JSON.</summary>
    public required string RawPayload { get; init; }

    /// <summary>First receipt timestamp.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>When processing completed (null = in-flight).</summary>
    public DateTimeOffset? ProcessedAt { get; init; }

    /// <summary>Four-status model: Received, Processing, Completed, Failed.</summary>
    public required IdempotencyStatus IdempotencyStatus { get; init; }

    /// <summary>Incremented on each reprocessing attempt by InboundRecoverySweep.</summary>
    public int AttemptCount { get; init; }

    /// <summary>Stores the latest failure reason for diagnostics.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>
    /// Stores the routed handler's <see cref="CommandResult.ErrorCode"/>
    /// / <see cref="CommandResult.ResponseText"/> when the pipeline
    /// returned <c>Succeeded=false</c> (terminal failure path of the
    /// hybrid retry contract — "return = terminal"). Distinct from
    /// <see cref="ErrorDetail"/>, which captures uncaught pipeline
    /// exceptions ("throw = retryable"). <c>null</c> on the success
    /// path.
    /// </summary>
    public string? HandlerErrorDetail { get; init; }

    /// <summary>
    /// Request-scoped trace identifier carried with the inbound webhook
    /// (sourced from the <c>X-Correlation-ID</c> header, an ambient
    /// <see cref="System.Diagnostics.Activity"/>, or a freshly-generated
    /// <see cref="Guid"/>). Persisted so the asynchronous pipeline leg
    /// (dispatcher / recovery sweep) can re-use the same id when
    /// emitting the <see cref="MessengerEvent"/>, satisfying the
    /// "All messages include trace/correlation ID" acceptance criterion.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Timestamp (UTC) the row most recently transitioned into
    /// <see cref="IdempotencyStatus.Processing"/>. Set by
    /// <see cref="IInboundUpdateStore.TryMarkProcessingAsync"/> alongside
    /// the status change in the same atomic UPDATE; cleared by
    /// <see cref="IInboundUpdateStore.MarkCompletedAsync"/>,
    /// <see cref="IInboundUpdateStore.MarkFailedAsync"/>,
    /// <see cref="IInboundUpdateStore.ReleaseProcessingAsync"/>, and
    /// <see cref="IInboundUpdateStore.ResetInterruptedAsync"/> so a stale
    /// value cannot survive across a Received→Processing→Received cycle.
    /// </summary>
    /// <remarks>
    /// <b>Lease semantic — iter-5 evaluator item 3.</b> The recovery
    /// sweep's <see cref="IInboundUpdateStore.ReclaimStaleProcessingAsync"/>
    /// pivots on this column to detect orphaned Processing rows that
    /// the one-shot startup reset (<c>InboundUpdateRecoveryStartup</c>)
    /// cannot reach — process crashed mid-pipeline AFTER startup ran,
    /// or a <c>ReleaseProcessingAsync</c> exception swallowed the
    /// cancellation release. Without this column a row claimed by a
    /// crashed worker would remain in Processing until the next host
    /// restart; with it, the periodic sweep reclaims it after the
    /// configured threshold (default 30 minutes, far above the story's
    /// 2-second P95 send latency, so a healthy long-running handler
    /// will never be falsely reset).
    /// <para>
    /// <b>Nullable on purpose</b>: legacy rows persisted before this
    /// column existed (or hand-seeded test rows) carry a null value;
    /// the reclaim query treats null as "stale" so legacy Processing
    /// rows do not strand silently.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ProcessingStartedAt { get; init; }
}
