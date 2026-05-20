// -----------------------------------------------------------------------
// <copyright file="SlackInboundResolvedCorrelationContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;

/// <summary>
/// Ambient (<see cref="AsyncLocal{T}"/>) slot for the business
/// correlation id resolved during inbound dispatch. The
/// <see cref="SlackInteractionHandler"/> stamps it after looking up the
/// <see cref="Entities.SlackThreadMapping"/> for a thread-bound click /
/// modal submission; <see cref="SlackInboundProcessingPipeline"/>
/// resets it once per envelope, and
/// <see cref="SlackInboundAuditRecorder"/> reads it when stamping the
/// <c>CorrelationId</c> column on the success / duplicate audit row.
/// </summary>
/// <remarks>
/// <para>
/// Stage 8.2 (story acceptance criterion AC-6: "every agent/human
/// exchange is queryable by correlation ID"). The transport-derived
/// idempotency key (<c>interact:&lt;...&gt;</c> for an interaction
/// envelope) is unique per click and therefore CANNOT serve as the
/// business correlation id that ties the inbound click back to the
/// originating <c>/agent ask</c> command and the agent's outbound
/// replies. Without this hop the audit row for the inbound interaction
/// would land under its own <c>interact:</c> key and the brief's
/// "single-correlation-id query returns the full exchange" promise
/// (architecture.md §3.5, tech-spec.md §5.4, e2e-scenarios.md scenario
/// 12.1) would not hold.
/// </para>
/// <para>
/// <b>Why <see cref="AsyncLocal{T}"/>.</b> The
/// <see cref="SlackInboundProcessingPipeline"/> calls the handler
/// directly via a singleton dependency -- no per-envelope DI scope
/// exists -- so a scoped service would have to be wrapped in a
/// per-envelope service scope (~50 lines of plumbing in the ingestor
/// and the pipeline). <see cref="AsyncLocal{T}"/> already flows
/// through async call chains, and the pipeline's
/// <see cref="Reset"/> at the top of <c>ProcessAsync</c> isolates
/// concurrent envelopes (the AsyncLocal value is copied-on-write per
/// logical task, so a sibling pipeline invocation cannot observe this
/// envelope's value).
/// </para>
/// <para>
/// <b>Fail-safe.</b> When no handler stamps a value (event /
/// command paths, or an interaction without a resolvable thread
/// mapping), the audit recorder falls back to its previous behaviour
/// of using <see cref="Transport.SlackInboundEnvelope.IdempotencyKey"/>.
/// The change is therefore strictly additive: callers that did not
/// previously have a resolved id continue to behave identically.
/// </para>
/// </remarks>
internal static class SlackInboundResolvedCorrelationContext
{
    private static readonly AsyncLocal<Holder?> CurrentHolder = new();

    /// <summary>
    /// Clears any previously-stamped value for the current async flow.
    /// Call once at the top of every pipeline dispatch so a stale value
    /// from an earlier envelope on the same logical task cannot leak
    /// into the next audit row.
    /// </summary>
    public static void Reset()
    {
        CurrentHolder.Value = new Holder();
    }

    /// <summary>
    /// Stamps the resolved business correlation id for the in-flight
    /// envelope. Auto-creates a holder when one is not already pinned
    /// on the current async flow so unit tests that invoke a handler
    /// directly (without going through
    /// <see cref="SlackInboundProcessingPipeline"/>'s top-of-dispatch
    /// <see cref="Reset"/>) still propagate the stamp to downstream
    /// audit recorders within the same async chain. The auto-create
    /// is per-flow (the assignment to <see cref="AsyncLocal{T}.Value"/>
    /// flows forward into the current logical task only), so it cannot
    /// leak into sibling pipeline invocations running on parallel
    /// xUnit collections.
    /// </summary>
    /// <param name="value">
    /// The resolved correlation id. <see langword="null"/> or empty is
    /// ignored so callers do not have to guard each
    /// <c>ResolveCorrelationIdAsync</c> result manually.
    /// </param>
    public static void Set(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Holder? holder = CurrentHolder.Value;
        if (holder is null)
        {
            // Iter-2 evaluator item #2 (STRUCTURAL): the previous
            // "no-op when no Reset" guard prevented Set from
            // populating the slot when a handler ran outside the
            // pipeline (e.g. SlackInteractionHandlerTests calling
            // HandleAsync directly). That meant
            // SlackModalAuditRecorder / SlackInboundAuditRecorder
            // could never observe the thread-anchored correlation id
            // on the modal_open audit row written from
            // OpenCommentModalAsync. Auto-creating the holder here
            // keeps the AsyncLocal scoping intact (the new holder
            // flows only through the current task's continuations)
            // while ensuring downstream auditors see the value the
            // handler resolved.
            holder = new Holder();
            CurrentHolder.Value = holder;
        }

        holder.Value = value;
    }

    /// <summary>
    /// Returns the most recently <see cref="Set"/> value for the
    /// current async flow, or <see langword="null"/> when no handler
    /// stamped one (or the pipeline never called <see cref="Reset"/>).
    /// </summary>
    public static string? Get()
    {
        return CurrentHolder.Value?.Value;
    }

    /// <summary>
    /// Mutable reference type so a single <see cref="AsyncLocal{T}"/>
    /// slot can carry an in-place updatable value -- assigning the
    /// <see cref="AsyncLocal{T}.Value"/> from a child task would
    /// otherwise only mutate the child's copy and the pipeline's
    /// subsequent <see cref="Get"/> call would observe the cleared
    /// value from <see cref="Reset"/>.
    /// </summary>
    private sealed class Holder
    {
        public string? Value;
    }
}
