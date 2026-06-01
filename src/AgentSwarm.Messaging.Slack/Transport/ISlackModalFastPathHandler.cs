// -----------------------------------------------------------------------
// <copyright file="ISlackModalFastPathHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Synchronous fast-path executed by <see cref="SlackCommandsController"/>
/// for the modal-opening sub-commands (<c>review</c> and <c>escalate</c>).
/// The handler runs the idempotency check and the <c>views.open</c> Web
/// API call inline within the HTTP request lifecycle because Slack's
/// <c>trigger_id</c> expires within approximately three seconds of
/// issuance (architecture.md §5.3 and tech-spec.md §5.2).
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.1 introduces the contract AND ships the production-grade
/// default <see cref="DefaultSlackModalFastPathHandler"/>, which runs
/// the synchronous <c>auth + idempotency + views.open</c> pipeline
/// inline within the HTTP request lifecycle. Later stages add
/// supporting infrastructure:
/// </para>
/// <list type="bullet">
///   <item><description>Stage 4.3 supersedes the in-process
///   <see cref="SlackInProcessIdempotencyStore"/> with the durable
///   <c>SlackIdempotencyGuard</c>.</description></item>
///   <item><description>Stage 5.2 supersedes
///   <see cref="DefaultSlackModalPayloadBuilder"/> with the typed
///   <c>SlackMessageRenderer</c>.</description></item>
///   <item><description>Stage 6.4 supersedes
///   <see cref="HttpClientSlackViewsOpenClient"/> with
///   <c>SlackDirectApiClient</c> (SlackNet-backed, shares rate-limit
///   state with the outbound dispatcher).</description></item>
/// </list>
/// <para>
/// A host that needs to OPT OUT of the real fast-path can register
/// <see cref="NoOpSlackModalFastPathHandler"/> BEFORE calling
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddSlackInboundTransport"/>;
/// the no-op handler returns
/// <see cref="SlackModalFastPathResult.AsyncFallback"/>, which the
/// controller surfaces as an ephemeral error to the user (modal
/// commands cannot be processed async because Slack's
/// <c>trigger_id</c> expires within ~3 seconds).
/// </para>
/// </remarks>
internal interface ISlackModalFastPathHandler
{
    /// <summary>
    /// Runs the synchronous review / escalate fast-path against the
    /// supplied envelope. Implementations MUST complete within the
    /// 3-second Slack ACK budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler returns one of three results (see
    /// <see cref="SlackModalFastPathResult"/>):
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="SlackModalFastPathResult.Handled()"/>
    ///   / <see cref="SlackModalFastPathResult.Handled(IActionResult)"/>
    ///   on success or a recoverable failure for which the user has
    ///   already been told (via an ephemeral
    ///   <see cref="IActionResult"/>) what went wrong.</description></item>
    ///   <item><description><see cref="SlackModalFastPathResult.DuplicateAck"/>
    ///   when the same envelope was already processed within the
    ///   idempotency window; the controller silently ACKs with HTTP
    ///   200.</description></item>
    ///   <item><description><see cref="SlackModalFastPathResult.AsyncFallback"/>
    ///   ONLY by a handler that intentionally declines to process the
    ///   request inline (e.g., the opt-in
    ///   <see cref="NoOpSlackModalFastPathHandler"/> used by tests /
    ///   dev hosts). The
    ///   <see cref="SlackCommandsController"/> treats this as a
    ///   misconfiguration -- because Slack's <c>trigger_id</c> expires
    ///   within ~3 seconds (architecture.md §5.3), an async-queued
    ///   modal command can never succeed -- and surfaces an ephemeral
    ///   error to the invoking user; it does NOT enqueue the envelope.
    ///   Production handlers should NEVER return
    ///   <see cref="SlackModalFastPathResult.AsyncFallback"/>; if they
    ///   exceed the ACK budget they should return
    ///   <see cref="SlackModalFastPathResult.Handled(IActionResult)"/>
    ///   with an ephemeral "please retry" message
    ///   instead.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="envelope">Normalized inbound envelope produced by
    /// <see cref="SlackInboundEnvelopeFactory"/>.</param>
    /// <param name="httpContext">The active HTTP context. The handler
    /// MAY read <see cref="HttpContext.Items"/> stamps set by the
    /// signature middleware or authorization filter.</param>
    /// <param name="ct">Request-aborted cancellation token.</param>
    Task<SlackModalFastPathResult> HandleAsync(
        SlackInboundEnvelope envelope,
        HttpContext httpContext,
        CancellationToken ct);
}

/// <summary>
/// Result returned by <see cref="ISlackModalFastPathHandler.HandleAsync"/>.
/// </summary>
/// <param name="ResultKind">Discriminator on how the controller should
/// finish the HTTP response.</param>
/// <param name="ActionResult">Optional pre-built
/// <see cref="IActionResult"/> when the handler needs to return a
/// non-trivial body (e.g., an ephemeral error). When
/// <see langword="null"/>, the controller writes the default empty
/// HTTP 200.</param>
internal readonly record struct SlackModalFastPathResult(
    SlackModalFastPathResultKind ResultKind,
    IActionResult? ActionResult)
{
    /// <summary>
    /// Handler accepted ownership of the envelope -- the controller
    /// MUST NOT enqueue it for async processing.
    /// </summary>
    public static SlackModalFastPathResult Handled() => new(SlackModalFastPathResultKind.Handled, null);

    /// <summary>
    /// Handler accepted ownership and provides a custom HTTP response
    /// (e.g., ephemeral error body when <c>views.open</c> failed).
    /// </summary>
    public static SlackModalFastPathResult Handled(IActionResult result)
        => new(SlackModalFastPathResultKind.Handled, result);

    /// <summary>
    /// Handler declined to process the request inline -- typically the
    /// opt-in <see cref="NoOpSlackModalFastPathHandler"/> in tests /
    /// dev hosts. The <see cref="SlackCommandsController"/> surfaces an
    /// ephemeral error to the invoking user and does NOT enqueue the
    /// envelope (Slack's <c>trigger_id</c> expires before any async
    /// dequeue can run, architecture.md §5.3). Production handlers
    /// should never return this value.
    /// </summary>
    public static SlackModalFastPathResult AsyncFallback => new(SlackModalFastPathResultKind.AsyncFallback, null);

    /// <summary>
    /// Handler detected a duplicate and the controller should silently
    /// ACK with HTTP 200 without enqueuing. Audit recording is the
    /// handler's responsibility.
    /// </summary>
    public static SlackModalFastPathResult DuplicateAck => new(SlackModalFastPathResultKind.DuplicateAck, null);
}

/// <summary>
/// Discriminator describing how the controller should finish a modal
/// fast-path call.
/// </summary>
internal enum SlackModalFastPathResultKind
{
    /// <summary>Handler completed; controller returns HTTP 200.</summary>
    Handled = 0,

    /// <summary>
    /// Handler not registered or not yet ready; the controller will
    /// surface an ephemeral error to the user (modal commands cannot be
    /// processed async because Slack's <c>trigger_id</c> expires within
    /// ~3 seconds). The controller MUST NOT enqueue the envelope.
    /// </summary>
    AsyncFallback = 1,

    /// <summary>Handler detected a duplicate; controller returns HTTP 200 with no enqueue.</summary>
    DuplicateAck = 2,
}
