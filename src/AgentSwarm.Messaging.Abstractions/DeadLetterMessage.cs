// -----------------------------------------------------------------------
// <copyright file="DeadLetterMessage.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Abstractions;

using System;

/// <summary>
/// Stage 4.2 — discriminator tracking whether the operator alert for
/// a dead-letter row has been dispatched to the secondary channel.
/// Per architecture.md §3.1 the dead-letter ledger drives an
/// out-of-band alerting loop; this enum is the column the loop
/// pivots on.
/// </summary>
public enum DeadLetterAlertStatus
{
    /// <summary>
    /// Row was just inserted; the alerting loop has not yet attempted
    /// to dispatch an out-of-band notification for it.
    /// </summary>
    Pending,

    /// <summary>
    /// The alerting loop has dispatched the notification (e.g. via
    /// <see cref="IAlertService.SendAlertAsync"/>); see
    /// <see cref="DeadLetterMessage.AlertSentAt"/> for the
    /// dispatch timestamp.
    /// </summary>
    Sent,

    /// <summary>
    /// An operator has explicitly acknowledged the alert (e.g. via a
    /// future replay-or-suppress workflow). Reserved for a future
    /// operator UI workstream; not written by Stage 4.2.
    /// </summary>
    Acknowledged,
}

/// <summary>
/// Stage 4.2 iter-2 evaluator item 1 — replay state for a dead-letter
/// row. Architecture.md §3.1 line 391 enumerates the four states an
/// operator workflow can drive a dead-letter row through. Defined in
/// <c>AgentSwarm.Messaging.Abstractions</c> so a future replay
/// orchestrator (out-of-scope for Stage 4.2) can reference the
/// state machine without taking a dependency on
/// <c>AgentSwarm.Messaging.Core</c>.
/// </summary>
public enum DeadLetterReplayStatus
{
    /// <summary>
    /// Row was just inserted; no replay attempt has been initiated.
    /// Default state for every Stage 4.2 <see cref="DeadLetterMessage"/>
    /// write.
    /// </summary>
    None,

    /// <summary>
    /// An operator has re-enqueued the original payload into the
    /// outbound queue via a future replay workflow; the new outbox
    /// row's <see cref="OutboundMessage.CorrelationId"/> is captured
    /// on <see cref="DeadLetterMessage.ReplayCorrelationId"/>.
    /// </summary>
    Queued,

    /// <summary>
    /// The replayed send completed successfully — the dead-letter
    /// row remains for audit but is no longer pending operator
    /// action.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The replayed send failed again; the operator must triage
    /// further. The original dead-letter row stays linked to the
    /// failed replay via <see cref="DeadLetterMessage.ReplayCorrelationId"/>.
    /// </summary>
    Failed,
}

/// <summary>
/// Stage 4.2 — durable dead-letter row written by the
/// <c>OutboundQueueProcessor</c> when an <see cref="OutboundMessage"/>
/// exhausts <see cref="OutboundMessage.MaxAttempts"/> or hits a
/// non-retryable failure category. Persists the full failure context
/// (payload, envelope, attempt count, final error, severity, source
/// type, correlation id) so the operator dead-letter audit screen
/// can replay or triage the failed send without round-tripping
/// through the outbox row, and so the secondary alert channel has a
/// stable reference for follow-up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate ledger from <see cref="OutboundDeadLetterRecord"/>.</b>
/// <see cref="OutboundDeadLetterRecord"/> is the
/// <c>TelegramMessageSender</c>-owned ledger keyed by
/// (<c>ChatId</c>, <c>CorrelationId</c>) — it captures sender-side
/// retry exhaustion regardless of which outbox row (if any) drove the
/// send. <see cref="DeadLetterMessage"/> is the Stage 4.2 outbox-row
/// companion keyed by <see cref="OriginalMessageId"/>: one row per
/// outbox row that the <c>OutboundQueueProcessor</c> gives up on.
/// Both ledgers co-exist by design — the sender's ledger survives a
/// processor restart that loses the outbox MessageId; the processor's
/// ledger gives the operator audit screen a 1-to-1 outbox→DLQ
/// mapping. See architecture.md §3.1 <c>DeadLetterMessage</c> for the
/// full field model rationale.
/// </para>
/// <para>
/// <b>CorrelationId guard.</b> Validated via
/// <see cref="CorrelationIdValidation.Require"/> at construction so a
/// dead-letter row cannot land with a blank trace id — the operator
/// pivot from alert into traces depends on a non-blank correlation
/// id (matches the same guard on <see cref="OutboundMessage"/> and
/// <see cref="OutboundDeadLetterRecord"/>).
/// </para>
/// </remarks>
public sealed record DeadLetterMessage
{
    private readonly string _correlationId = null!;

    /// <summary>
    /// Synthetic GUID primary key. Generated by the writer
    /// (<c>OutboundQueueProcessor</c>) at insert time so the database
    /// does not have to coordinate identity values across concurrent
    /// workers.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// <see cref="OutboundMessage.MessageId"/> of the outbox row that
    /// was dead-lettered. UNIQUE — exactly one
    /// <see cref="DeadLetterMessage"/> per outbox row (architecture.md
    /// §3.1 "<c>UNIQUE (OriginalMessageId)</c>" constraint). The
    /// dead-lettered outbox row itself stays in
    /// <see cref="OutboundMessageStatus.DeadLettered"/> for the audit
    /// trail; this ledger row is the operator-facing entry point.
    /// </summary>
    public required Guid OriginalMessageId { get; init; }

    /// <summary>
    /// Copied from <see cref="OutboundMessage.IdempotencyKey"/> for
    /// cross-reference. Useful when an operator inspects the
    /// dead-letter row in isolation (without joining back to
    /// <c>outbox</c>) to identify the logical send.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Target Telegram chat — copied from the outbox row.</summary>
    public required long ChatId { get; init; }

    /// <summary>
    /// Verbatim copy of <see cref="OutboundMessage.Payload"/>. For
    /// <see cref="OutboundSourceType.CommandAck"/>,
    /// <see cref="OutboundSourceType.StatusUpdate"/>, and
    /// <see cref="OutboundSourceType.Alert"/> source types this is
    /// the pre-rendered MarkdownV2 text. For
    /// <see cref="OutboundSourceType.Question"/> it is the
    /// human-readable preview only — the actual send content is
    /// reconstructed from <see cref="SourceEnvelopeJson"/>.
    /// </summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Copied from <see cref="OutboundMessage.SourceEnvelopeJson"/>.
    /// Non-null only for <see cref="OutboundSourceType.Question"/>
    /// and <see cref="OutboundSourceType.Alert"/>. Preserves the
    /// original source envelope so a replay can reconstruct the send
    /// without external state.
    /// </summary>
    public string? SourceEnvelopeJson { get; init; }

    /// <summary>Copied from <see cref="OutboundMessage.Severity"/>.</summary>
    public required MessageSeverity Severity { get; init; }

    /// <summary>Copied from <see cref="OutboundMessage.SourceType"/>.</summary>
    public required OutboundSourceType SourceType { get; init; }

    /// <summary>Copied from <see cref="OutboundMessage.SourceId"/>.</summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// Iter-2 evaluator item 4 — logical agent identity for the
    /// dead-lettered send. e2e-scenarios.md "Persistent failure
    /// dead-letters the message" (line 244) mandates the dead-letter
    /// row include the AgentId so the operator audit screen can
    /// pivot per-agent without re-parsing <see cref="SourceEnvelopeJson"/>
    /// or joining back to the outbox row. Populated from
    /// <see cref="AgentQuestion.AgentId"/> for a <see cref="OutboundSourceType.Question"/>
    /// source, from <see cref="MessengerMessage.AgentId"/> for an
    /// <see cref="OutboundSourceType.Alert"/> source, and may be null
    /// for the <see cref="OutboundSourceType.CommandAck"/> /
    /// <see cref="OutboundSourceType.StatusUpdate"/> source types
    /// whose envelopes do not always identify a single owning agent.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// End-to-end trace id of the original message — same value as
    /// <see cref="OutboundMessage.CorrelationId"/>. Validated
    /// non-blank at construction.
    /// </summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }

    /// <summary>
    /// Total delivery attempts consumed before the row was
    /// dead-lettered. Equals
    /// <c>OutboundMessage.AttemptCount</c> at the moment the
    /// processor declared the budget exhausted (or the failure
    /// permanent).
    /// </summary>
    public required int AttemptCount { get; init; }

    /// <summary>
    /// Free-form text of the last observed error message, bounded to
    /// 2048 chars at write time. The processor formats this as
    /// <c>"[FailureCategory] message"</c> for consistency with the
    /// outbox row's <c>ErrorDetail</c> column.
    /// </summary>
    public required string FinalError { get; init; }

    /// <summary>
    /// Stage 4.2 iter-2 evaluator item 1 — architecture.md §3.1 line
    /// 386: <c>AttemptTimestamps</c>. JSON array of ISO-8601 UTC
    /// timestamps, one per attempt. Example:
    /// <c>["2026-05-11T18:00:00Z","2026-05-11T18:00:02Z",
    /// "2026-05-11T18:00:06Z","2026-05-11T18:00:14Z",
    /// "2026-05-11T18:00:30Z"]</c>. Sourced from the outbox row's
    /// accumulated <see cref="OutboundMessage.AttemptHistoryJson"/>
    /// at dead-letter time so the operator audit screen can plot
    /// the retry timeline without joining back to the outbox table.
    /// Defaults to <c>"[]"</c> when the row was created via a
    /// callsite that did not supply attempt history (legacy callers
    /// constructing the entity directly in tests).
    /// </summary>
    public string AttemptTimestamps { get; init; } = "[]";

    /// <summary>
    /// Stage 4.2 iter-2 evaluator item 1 — architecture.md §3.1 line
    /// 388: <c>ErrorHistory</c>. JSON array of
    /// <c>{ "attempt": int, "timestamp": DateTimeOffset, "error":
    /// string, "httpStatus": int? }</c> objects — one per attempt.
    /// Preserves the full failure progression for diagnostics so
    /// the operator can distinguish "transient 5xx that eventually
    /// timed out" from "first attempt blew up on a malformed
    /// envelope and every retry hit the same parse error". Defaults
    /// to <c>"[]"</c> for the same legacy-construction reason as
    /// <see cref="AttemptTimestamps"/>.
    /// </summary>
    public string ErrorHistory { get; init; } = "[]";

    /// <summary>
    /// Discriminator for the failure mode — see
    /// <see cref="OutboundFailureCategory"/>. The dead-letter audit
    /// screen branches on this field; the operator runbook routes
    /// permanent failures (chat blocked) to the bot-token /
    /// allowlist desk and transient failures (transport, rate-limit)
    /// to the infrastructure desk.
    /// </summary>
    public required OutboundFailureCategory FailureCategory { get; init; }

    /// <summary>
    /// Tracks whether the operator alert for this row has been
    /// dispatched. Defaults to <see cref="DeadLetterAlertStatus.Pending"/>
    /// at insert; the processor flips it to
    /// <see cref="DeadLetterAlertStatus.Sent"/> immediately after
    /// successfully invoking <see cref="IAlertService.SendAlertAsync"/>.
    /// </summary>
    public DeadLetterAlertStatus AlertStatus { get; init; } = DeadLetterAlertStatus.Pending;

    /// <summary>
    /// UTC instant the operator alert dispatch succeeded. Null while
    /// <see cref="AlertStatus"/> is <see cref="DeadLetterAlertStatus.Pending"/>.
    /// </summary>
    public DateTimeOffset? AlertSentAt { get; init; }

    /// <summary>
    /// Stage 4.2 iter-2 evaluator item 1 — architecture.md §3.1 line
    /// 391: <c>ReplayStatus</c>. Tracks manual replay attempts —
    /// <see cref="DeadLetterReplayStatus.None"/> means no operator
    /// has initiated a replay yet. Indexed at the database layer
    /// (architecture.md §3.1 line 399) so the operator audit screen
    /// can paginate the "replay-eligible" view (rows where
    /// <c>ReplayStatus = None</c>) without a full table scan.
    /// Defaults to <see cref="DeadLetterReplayStatus.None"/> at
    /// insert; Stage 4.2 itself does NOT mutate this column (the
    /// replay orchestrator is a future workstream).
    /// </summary>
    public DeadLetterReplayStatus ReplayStatus { get; init; } = DeadLetterReplayStatus.None;

    /// <summary>
    /// Stage 4.2 iter-2 evaluator item 1 — architecture.md §3.1 line
    /// 392: <c>ReplayCorrelationId</c>. <see cref="OutboundMessage.CorrelationId"/>
    /// of the replay attempt; links the replayed outbox row back to
    /// this dead-letter record. Null until a replay is initiated.
    /// Bounded to 128 chars at the persistence layer to match the
    /// canonical CorrelationId column width across every other
    /// entity in the messaging schema.
    /// </summary>
    public string? ReplayCorrelationId { get; init; }

    /// <summary>
    /// UTC instant the row was dead-lettered. Used for retention
    /// sweeps and for the "dead-lettered in the last 24h" operator
    /// screen.
    /// </summary>
    public required DateTimeOffset DeadLetteredAt { get; init; }

    /// <summary>
    /// Copied from <see cref="OutboundMessage.CreatedAt"/> so the
    /// dead-letter audit screen can compute the "time from enqueue to
    /// final failure" duration without joining back to the
    /// dead-lettered outbox row.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
