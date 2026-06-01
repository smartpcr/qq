// -----------------------------------------------------------------------
// <copyright file="ISlackIdempotencyGuard.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Atomically claims an idempotency key for the async inbound
/// ingestion pipeline. Implements architecture.md §4.4 verbatim;
/// the canonical EF-backed implementation is
/// <see cref="SlackIdempotencyGuard{TContext}"/> writing rows into the
/// <c>slack_inbound_request_record</c> table introduced by Stage 2.2.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The guard is the canonical dedup anchor for Slack's at-least-once
/// redelivery contract. Per architecture.md §2.6 / §4.4 the lease
/// semantics applied to a redelivered envelope depend on the
/// pre-existing row's <see cref="SlackInboundRequestProcessingStatus"/>:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       Terminal statuses (<see cref="SlackInboundRequestProcessingStatus.Completed"/>,
///       <see cref="SlackInboundRequestProcessingStatus.Failed"/>) and
///       the Stage 4.1 fast-path statuses (<see cref="SlackInboundRequestProcessingStatus.Reserved"/>,
///       <see cref="SlackInboundRequestProcessingStatus.ModalOpened"/>,
///       <see cref="SlackInboundRequestProcessingStatus.Received"/>) are
///       reported as duplicates -- the redelivery is silently dropped
///       so the handler does NOT run a second time.
///     </description>
///   </item>
///   <item>
///     <description>
///       A <see cref="SlackInboundRequestProcessingStatus.Processing"/>
///       row that is still within the configured stale-lease window
///       (<c>SlackConnectorOptions.Idempotency.StaleProcessingThresholdSeconds</c>,
///       default 300s) is DEFERRED -- a healthy in-flight worker still
///       owns the lease, so the redelivery is reported as a duplicate
///       (return <see langword="false"/>) to avoid preempting it.
///     </description>
///   </item>
///   <item>
///     <description>
///       A <see cref="SlackInboundRequestProcessingStatus.Processing"/>
///       row OLDER than the stale-lease window is RECLAIMED via OCC --
///       a worker that crashed mid-flow no longer leaves the row stuck
///       forever; the redelivery acquires a fresh lease and re-dispatches.
///       Return <see langword="true"/>.
///     </description>
///   </item>
/// </list>
/// <para>
/// <see cref="TryAcquireAsync"/> is the only mutating call on the
/// happy path: it inserts a new
/// <see cref="Entities.SlackInboundRequestRecord"/> with status
/// <see cref="SlackInboundRequestProcessingStatus.Processing"/> and
/// returns <see langword="true"/>. A duplicate or deferred row
/// returns <see langword="false"/> without modifying the existing
/// state; a reclaimed stale lease bumps
/// <see cref="Entities.SlackInboundRequestRecord.FirstSeenAt"/> and
/// returns <see langword="true"/>.
/// </para>
/// <para>
/// <see cref="MarkCompletedAsync"/> and <see cref="MarkFailedAsync"/>
/// transition the previously-acquired row to a terminal state and
/// stamp <see cref="Entities.SlackInboundRequestRecord.CompletedAt"/>.
/// Implementations MUST NOT throw on a missing row -- in normal
/// operation the row exists, but a host that lost its database
/// between TryAcquire and MarkCompleted should not crash the ingestor.
/// </para>
/// </remarks>
internal interface ISlackIdempotencyGuard
{
    /// <summary>
    /// Attempts to claim the supplied idempotency key for the
    /// supplied envelope. Returns <see langword="true"/> when the
    /// envelope is either new (no row exists) OR an existing
    /// <see cref="SlackInboundRequestProcessingStatus.Processing"/>
    /// lease is older than the configured stale-lease window and is
    /// being reclaimed for crash recovery (the original worker is
    /// presumed dead, so the redelivery becomes the new lease
    /// owner). Returns <see langword="false"/> in two distinct
    /// lease scenarios that share the &quot;drop the envelope&quot; outcome
    /// but mean different things to an operator:
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>
    ///     <term>True duplicate</term>
    ///     <description>
    ///       The existing row is in a terminal status
    ///       (<see cref="SlackInboundRequestProcessingStatus.Completed"/>,
    ///       <see cref="SlackInboundRequestProcessingStatus.Failed"/>)
    ///       or a Stage 4.1 fast-path status
    ///       (<see cref="SlackInboundRequestProcessingStatus.Reserved"/>,
    ///       <see cref="SlackInboundRequestProcessingStatus.ModalOpened"/>,
    ///       <see cref="SlackInboundRequestProcessingStatus.Received"/>).
    ///       The handler has already run (or the fast-path already
    ///       owns the request); the redelivery is genuinely
    ///       redundant and the ingestor silently drops it.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Deferred (live lease)</term>
    ///     <description>
    ///       The existing row is
    ///       <see cref="SlackInboundRequestProcessingStatus.Processing"/>
    ///       AND younger than
    ///       <c>SlackConnectorOptions.Idempotency.StaleProcessingThresholdSeconds</c>.
    ///       A healthy in-flight worker still owns the lease, so
    ///       the redelivery is DEFERRED to avoid preempting it. The
    ///       ingestor still drops the envelope; the audit row uses
    ///       <c>outcome = duplicate</c> as a shared no-handler-ran
    ///       marker, but operators can disambiguate by looking at
    ///       the existing row's status (still <c>processing</c>
    ///       indicates a deferred redelivery, not a finished one).
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="envelope">Envelope being processed. The guard
    /// persists subset fields (team_id, channel_id, user_id,
    /// source_type, raw payload hash, first-seen timestamp) so an
    /// operator inspecting the row has enough triage context.</param>
    /// <param name="ct">Cancellation token bound to the ingestor's
    /// stopping token.</param>
    Task<bool> TryAcquireAsync(SlackInboundEnvelope envelope, CancellationToken ct);

    /// <summary>
    /// Marks a previously-acquired row as
    /// <see cref="SlackInboundRequestProcessingStatus.Completed"/>
    /// and stamps
    /// <see cref="Entities.SlackInboundRequestRecord.CompletedAt"/>
    /// with the current UTC timestamp. Implementations MUST apply a
    /// bounded retry budget on transient backing-store failures so a
    /// successfully-handled envelope does not stay in the non-
    /// terminal <c>processing</c> state (where the stale-reclaim
    /// path would later re-execute the handler). After the retry
    /// budget is exhausted the call returns silently and the
    /// implementation is expected to log critically so operators can
    /// reconcile the row manually. A missing row is logged but does
    /// not throw.
    /// </summary>
    Task MarkCompletedAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Marks a previously-acquired row as
    /// <see cref="SlackInboundRequestProcessingStatus.Failed"/> after
    /// the retry budget was exhausted (the envelope has been moved
    /// to the dead-letter queue). Same bounded-retry + best-effort
    /// semantics as <see cref="MarkCompletedAsync"/>.
    /// </summary>
    Task MarkFailedAsync(string idempotencyKey, CancellationToken ct);
}
