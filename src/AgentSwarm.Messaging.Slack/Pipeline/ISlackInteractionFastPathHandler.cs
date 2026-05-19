// -----------------------------------------------------------------------
// <copyright file="ISlackInteractionFastPathHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Synchronous fast-path executed by
/// <see cref="SlackInteractionsController"/> for interactive payloads
/// whose downstream side-effect MUST run inside Slack's 3-second
/// <c>trigger_id</c> lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The Slack <c>trigger_id</c> attached to a Block Kit button click
/// expires within approximately three seconds of issuance
/// (architecture.md §5.3, tech-spec.md §5.2). The async
/// <see cref="SlackInteractionHandler"/> running on the inbound queue
/// drain cannot meet that deadline because
/// <see cref="SlackInboundEnqueueScheduler.ScheduleAfterAck"/>
/// intentionally defers the enqueue until AFTER the HTTP response
/// has flushed to Slack -- so any <c>views.open</c> call from the
/// async path races against trigger expiry. Without a synchronous
/// fast-path the inevitable failure is swallowed and the user's
/// click is lost without a publish OR a fallback message.
/// </para>
/// <para>
/// The fast-path mirrors <see cref="ISlackModalFastPathHandler"/>
/// for slash commands: it runs INSIDE the HTTP request lifecycle so
/// the <c>views.open</c> call happens while <c>trigger_id</c> is
/// still valid. The default implementation
/// (<see cref="DefaultSlackInteractionFastPathHandler"/>) acts on
/// block_actions payloads whose <c>block_id</c> encodes a
/// <c>RequiresComment</c> action -- those are the only interactions
/// that need an immediate <c>views.open</c>. All other interactive
/// payloads (normal button clicks, modal submissions) return
/// <see cref="SlackInteractionFastPathResult.AsyncFallback"/> so the
/// controller continues with the existing post-ACK enqueue path.
/// </para>
/// </remarks>
internal interface ISlackInteractionFastPathHandler
{
    /// <summary>
    /// Runs the synchronous fast-path against
    /// <paramref name="envelope"/>. Implementations MUST complete
    /// well within the 3-second Slack ACK budget.
    /// </summary>
    /// <param name="envelope">Normalised inbound envelope produced by
    /// <see cref="SlackInboundEnvelopeFactory"/>.</param>
    /// <param name="httpContext">Active HTTP context. Implementations
    /// MAY read <see cref="HttpContext.Items"/> stamps set by the
    /// signature middleware or authorization filter.</param>
    /// <param name="ct">Request-aborted cancellation token.</param>
    Task<SlackInteractionFastPathResult> HandleAsync(
        SlackInboundEnvelope envelope,
        HttpContext httpContext,
        CancellationToken ct);
}

/// <summary>
/// Result returned by
/// <see cref="ISlackInteractionFastPathHandler.HandleAsync"/>.
/// </summary>
/// <param name="ResultKind">Discriminator on how the controller should
/// finish the HTTP response.</param>
/// <param name="ActionResult">Optional pre-built
/// <see cref="IActionResult"/> the controller returns verbatim when
/// the fast-path needs to surface an ephemeral error to the user.
/// When <see langword="null"/>, the controller writes the default
/// empty HTTP 200.</param>
internal readonly record struct SlackInteractionFastPathResult(
    SlackInteractionFastPathResultKind ResultKind,
    IActionResult? ActionResult)
{
    /// <summary>
    /// Fast-path accepted ownership of the envelope -- the
    /// controller MUST NOT enqueue it for async processing
    /// (e.g., the <c>views.open</c> for a RequiresComment button
    /// has already been issued, and the resulting view_submission
    /// will arrive as a separate inbound envelope).
    /// </summary>
    public static SlackInteractionFastPathResult Handled() =>
        new(SlackInteractionFastPathResultKind.Handled, null);

    /// <summary>
    /// Fast-path accepted ownership and supplies a custom HTTP
    /// response (e.g., ephemeral error body when
    /// <c>views.open</c> failed).
    /// </summary>
    public static SlackInteractionFastPathResult Handled(IActionResult result) =>
        new(SlackInteractionFastPathResultKind.Handled, result);

    /// <summary>
    /// Fast-path declined to process the request inline -- the
    /// controller MUST fall through to the async enqueue path
    /// (the normal flow for plain button clicks and for modal
    /// view submissions whose side-effects are not
    /// <c>trigger_id</c>-bound).
    /// </summary>
    public static SlackInteractionFastPathResult AsyncFallback =>
        new(SlackInteractionFastPathResultKind.AsyncFallback, null);
}

/// <summary>
/// Discriminator on <see cref="SlackInteractionFastPathResult"/>.
/// </summary>
internal enum SlackInteractionFastPathResultKind
{
    /// <summary>
    /// Fast-path completed its work inline; controller returns HTTP 200
    /// without enqueuing the envelope.
    /// </summary>
    Handled = 0,

    /// <summary>
    /// Fast-path took no action; controller MUST run the normal
    /// post-ACK enqueue path so the async ingestor processes the
    /// envelope.
    /// </summary>
    AsyncFallback = 1,
}
