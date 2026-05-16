// -----------------------------------------------------------------------
// <copyright file="SlackInboundRequestProcessingStatus.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// String constants written to
/// <see cref="SlackInboundRequestRecord.ProcessingStatus"/>. Single
/// source of truth so the idempotency guard, the modal fast-path
/// store, and audit queries all agree on the literal values.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Architecture.md §3.3 lists the canonical lifecycle values
/// (<see cref="Received"/>, <see cref="Processing"/>,
/// <see cref="Completed"/>, <see cref="Failed"/>); Stage 4.1's modal
/// fast-path adds <see cref="Reserved"/> and <see cref="ModalOpened"/>
/// for the synchronous in-request lifecycle that runs ahead of the
/// async ingestor.
/// </para>
/// <para>
/// The constants are kept as <see cref="string"/> values (not a .NET
/// enum) so the persisted column stays portable across stores that do
/// not support enums natively and human triage queries against
/// <c>processing_status</c> remain readable.
/// </para>
/// </remarks>
internal static class SlackInboundRequestProcessingStatus
{
    /// <summary>
    /// Initial value written by the transport receiver when the row
    /// is first observed but no handler has yet been selected.
    /// Currently unused by the canonical Stage 4.3 ingestor path
    /// (which writes <see cref="Processing"/> directly) but preserved
    /// for hosts that prefer a two-phase insert.
    /// </summary>
    public const string Received = "received";

    /// <summary>
    /// Written by <see cref="SlackIdempotencyGuard{TContext}"/> at
    /// <see cref="ISlackIdempotencyGuard.TryAcquireAsync"/> time --
    /// the ingestor has claimed the envelope and is currently
    /// dispatching it to a handler.
    /// </summary>
    public const string Processing = "processing";

    /// <summary>
    /// Terminal success marker stamped by
    /// <see cref="ISlackIdempotencyGuard.MarkCompletedAsync"/> after
    /// the handler returned without throwing.
    /// </summary>
    public const string Completed = "completed";

    /// <summary>
    /// Terminal failure marker stamped by
    /// <see cref="ISlackIdempotencyGuard.MarkFailedAsync"/> after the
    /// retry budget was exhausted (and the envelope was forwarded to
    /// the dead-letter queue).
    /// </summary>
    public const string Failed = "failed";

    /// <summary>
    /// Reserved by the Stage 4.1 modal fast-path
    /// (<c>EntityFrameworkSlackFastPathIdempotencyStore&lt;TContext&gt;.ProcessingStatusReserved</c>)
    /// while <c>views.open</c> is still in flight inside the HTTP
    /// request lifetime. The ingestor treats this as "already owned
    /// by someone else" and silently drops the duplicate envelope.
    /// The literal value MUST stay in lockstep with that constant --
    /// pinned by
    /// <c>SlackInboundRequestProcessingStatus_StaysInLockstepWithFastPathConstants</c>
    /// in the test project so a future rename of either side is
    /// caught at build time.
    /// </summary>
    public const string Reserved = "reserved";

    /// <summary>
    /// Stamped by the Stage 4.1 fast-path
    /// (<c>EntityFrameworkSlackFastPathIdempotencyStore&lt;TContext&gt;.ProcessingStatusModalOpened</c>)
    /// after <c>views.open</c> returned success. The ingestor treats
    /// this row as terminal -- a Slack retry that arrives at the
    /// async pipeline AFTER the modal already opened MUST NOT trigger
    /// a second views.open or any other duplicate work. See the
    /// Reserved docstring for the lockstep test pin.
    /// </summary>
    public const string ModalOpened = "modal_opened";

    /// <summary>
    /// Non-reclaimable disposition written by
    /// <see cref="SlackIdempotencyGuard{TContext}"/> when the handler
    /// returned successfully but every attempt to persist
    /// <see cref="Completed"/> via the EF change-tracker path failed
    /// AND a final raw <c>ExecuteUpdateAsync</c> attempt also could
    /// not reach <see cref="Completed"/>. The literal semantics:
    /// &quot;handler ran, persistence of completion failed, operator
    /// must reconcile -- but the stale-reclaim path MUST NOT
    /// re-dispatch this envelope because the handler has already
    /// observed it.&quot; Operators can distinguish a true handler
    /// failure (<see cref="Failed"/>) from a completion-persistence
    /// failure (this value) when triaging the audit log.
    /// </summary>
    /// <remarks>
    /// Iter 7 evaluator item #1: the prior bounded-retry budget on
    /// <see cref="ISlackIdempotencyGuard.MarkCompletedAsync"/> closed
    /// the common transient-blip window but left the residual
    /// exhaustion case in <see cref="Processing"/>, where the stale-
    /// reclaim path at <c>SlackIdempotencyGuard.TryReclaimStaleLeaseAsync</c>
    /// would eventually re-execute the handler. Adding a dedicated
    /// non-reclaimable disposition makes the duplicate-execution risk
    /// structurally impossible because the reclaim WHERE clause
    /// filters on <c>processing_status = 'processing'</c>.
    /// </remarks>
    public const string CompletionPersistFailed = "completion_persist_failed";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="status"/>
    /// represents a row the async ingestor must skip (because either
    /// the modal fast-path or a previous ingestor pass has already
    /// taken ownership of the work).
    /// </summary>
    public static bool IsAlreadyOwned(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return false;
        }

        return string.Equals(status, Processing, StringComparison.Ordinal)
            || string.Equals(status, Completed, StringComparison.Ordinal)
            || string.Equals(status, Failed, StringComparison.Ordinal)
            || string.Equals(status, Reserved, StringComparison.Ordinal)
            || string.Equals(status, ModalOpened, StringComparison.Ordinal)
            || string.Equals(status, Received, StringComparison.Ordinal)
            || string.Equals(status, CompletionPersistFailed, StringComparison.Ordinal);
    }
}
