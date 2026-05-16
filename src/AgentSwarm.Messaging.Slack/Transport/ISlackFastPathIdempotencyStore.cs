// -----------------------------------------------------------------------
// <copyright file="ISlackFastPathIdempotencyStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Pluggable idempotency check used by
/// <see cref="DefaultSlackModalFastPathHandler"/> while the modal
/// fast-path runs inside the HTTP request lifecycle. Replaces the
/// concrete <see cref="SlackInProcessIdempotencyStore"/> dependency so
/// the Worker host can pull forward Stage 4.3's durable
/// <c>SlackInboundRequestRecord</c>-backed guard without changing the
/// fast-path handler.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.1 evaluator iter-2 item 2 flagged that the iter-2
/// in-process-only store could not catch retries that span a process
/// restart or arrive at a different replica, so a duplicate
/// <c>/agent review TASK-42</c> could open a second modal. This
/// interface lets the Worker host bind a durable two-level
/// implementation (<see cref="CompositeSlackFastPathIdempotencyStore"/>)
/// without forcing the handler to know about the
/// <c>SlackInboundRequestRecord</c> table.
/// </para>
/// <para>
/// The interface returns a discriminator
/// (<see cref="SlackFastPathIdempotencyResult"/>) instead of a plain
/// <see cref="bool"/> so the caller can distinguish a true duplicate
/// from a "store is temporarily unavailable" condition. The fast-path
/// MUST degrade gracefully (continue with views.open) when the durable
/// store fails, because the alternative is failing every modal request
/// during a database blip -- but it MUST log the degradation so an
/// operator can spot the gap.
/// </para>
/// </remarks>
internal interface ISlackFastPathIdempotencyStore
{
    /// <summary>
    /// Attempts to claim the supplied idempotency key for the lifetime
    /// of the modal flow.
    /// </summary>
    /// <param name="key">Idempotency key derived per
    /// architecture.md §3.4 (e.g.,
    /// <c>cmd:{team}:{user}:/agent:{trigger_id}</c>).</param>
    /// <param name="envelope">The envelope being processed. Implementations
    /// may persist subset fields (team_id, source_type, etc.) for triage,
    /// but MUST treat <paramref name="key"/> as the primary dedup
    /// anchor.</param>
    /// <param name="lifetime">Optional TTL for the entry. When
    /// <see langword="null"/> the implementation's default lifetime is
    /// used.</param>
    /// <param name="ct">Cancellation token bound to the HTTP request.</param>
    ValueTask<SlackFastPathIdempotencyResult> TryAcquireAsync(
        string key,
        SlackInboundEnvelope envelope,
        TimeSpan? lifetime = null,
        CancellationToken ct = default);

    /// <summary>
    /// Forgets the supplied key so a retry following a failed
    /// <c>views.open</c> can succeed. Called by the fast-path handler
    /// on every failure path (Slack error, network error, missing
    /// configuration) so the user is not blocked by the previous
    /// attempt's reservation.
    /// </summary>
    ValueTask ReleaseAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Result returned by
/// <see cref="ISlackFastPathIdempotencyStore.TryAcquireAsync"/>.
/// </summary>
internal readonly record struct SlackFastPathIdempotencyResult(
    SlackFastPathIdempotencyOutcome Outcome,
    string? Diagnostic)
{
    /// <summary>The key was claimed; the caller may proceed.</summary>
    public static SlackFastPathIdempotencyResult Acquired() =>
        new(SlackFastPathIdempotencyOutcome.Acquired, null);

    /// <summary>
    /// The key was already held by a previous invocation; the caller
    /// MUST short-circuit with a silent ACK.
    /// </summary>
    public static SlackFastPathIdempotencyResult Duplicate(string? diagnostic = null) =>
        new(SlackFastPathIdempotencyOutcome.Duplicate, diagnostic);

    /// <summary>
    /// The durable store was unavailable (transient DB error). The
    /// caller proceeds without the durable check -- the in-process
    /// L1 store still gates rapid retries -- but the
    /// <paramref name="diagnostic"/> is logged so an operator can see
    /// the gap.
    /// </summary>
    public static SlackFastPathIdempotencyResult StoreUnavailable(string diagnostic) =>
        new(SlackFastPathIdempotencyOutcome.StoreUnavailable, diagnostic);

    /// <summary>Returns <see langword="true"/> when the caller may proceed.</summary>
    public bool ShouldProceed =>
        this.Outcome == SlackFastPathIdempotencyOutcome.Acquired
        || this.Outcome == SlackFastPathIdempotencyOutcome.StoreUnavailable;

    /// <summary>Returns <see langword="true"/> when a duplicate was detected.</summary>
    public bool IsDuplicate => this.Outcome == SlackFastPathIdempotencyOutcome.Duplicate;
}

/// <summary>
/// Three-way discriminator for
/// <see cref="SlackFastPathIdempotencyResult"/>.
/// </summary>
internal enum SlackFastPathIdempotencyOutcome
{
    /// <summary>Key was newly claimed; proceed with views.open.</summary>
    Acquired = 0,

    /// <summary>Key was already claimed; silent ACK.</summary>
    Duplicate = 1,

    /// <summary>Durable store failed; degrade to L1-only check and proceed.</summary>
    StoreUnavailable = 2,
}
