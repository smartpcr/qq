// -----------------------------------------------------------------------
// <copyright file="SlackEventsController.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// ASP.NET Core controller hosting the Slack Events API endpoint
/// (<c>POST /api/slack/events</c>). Implements Stage 4.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint handles two distinct payload shapes documented in
/// architecture.md §2.2.2 and §5.6:
/// </para>
/// <list type="number">
///   <item><description>The Slack Events API URL-verification handshake
///   (<c>type = url_verification</c>) which MUST be answered with the
///   matching <c>challenge</c> string and HTTP 200. The signature
///   middleware stamps
///   <see cref="SlackSignatureValidator.UrlVerificationItemKey"/> on the
///   <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> bag when
///   it accepts the handshake, and the
///   <see cref="SlackAuthorizationFilter"/> short-circuits on the same
///   marker -- so by the time the controller runs the body is still
///   buffered and the controller's only job is to echo the
///   challenge.</description></item>
///   <item><description>An Events API event callback
///   (<c>type = event_callback</c> wrapping an <c>app_mention</c>,
///   <c>message</c>, etc.). The controller normalises the payload via
///   <see cref="SlackInboundEnvelopeFactory"/>, enqueues the envelope
///   into <see cref="ISlackInboundQueue"/>, and immediately returns
///   HTTP 200 to satisfy Slack's 3-second ACK budget. Subsequent
///   processing (idempotency, dispatch, orchestrator call) is owned by
///   Stage 4.3's <c>SlackInboundIngestor</c>.</description></item>
/// </list>
/// </remarks>
[ApiController]
[Route("api/slack/events")]
[Consumes("application/json")]
[Produces("application/json")]
public sealed class SlackEventsController : ControllerBase
{
    /// <summary>
    /// Accepts a Slack Events API POST. Returns the URL-verification
    /// challenge for the handshake payload; otherwise normalises and
    /// enqueues the event for async processing and returns HTTP 200.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The controller's internal dependencies
    /// (<see cref="SlackInboundEnvelopeFactory"/>,
    /// <see cref="ISlackInboundQueue"/>) are resolved from
    /// <see cref="Microsoft.AspNetCore.Http.HttpContext.RequestServices"/>
    /// rather than constructor injection because the brief pins both
    /// types as <c>internal</c> (Stage 1.3 + Stage 4.1) yet MVC requires
    /// controller types to be public for discovery. Per-request service
    /// resolution is cheap (the singletons are already constructed) and
    /// keeps the public controller surface free of internal parameter
    /// types.
    /// </para>
    /// <para>
    /// <b>Hot-path optimisation</b> (PR review feedback): on the
    /// non-handshake path the controller used to invoke
    /// <see cref="SlackInboundPayloadParser.ParseEvent(string)"/>
    /// itself, and the envelope factory then re-parsed the same body
    /// internally -- two <see cref="System.Text.Json.JsonDocument"/>
    /// allocations per event under the 3-second Slack ACK budget. We
    /// now gate the controller-side parse behind a cheap ordinal
    /// substring pre-filter on <c>"url_verification"</c>: the handshake
    /// discriminator (<c>type = url_verification</c>) is a required
    /// literal in the Slack URL-verification body, so the substring is
    /// guaranteed present on the rare handshake path and almost never
    /// on the steady-state <c>event_callback</c> path. False positives
    /// (e.g., a user message that happens to contain the literal
    /// string) merely trigger one extra parse; false negatives are
    /// impossible because the Slack specification mandates the literal
    /// token. On the hot path the body is now parsed exactly once --
    /// inside the factory -- satisfying the reviewer's "parse once"
    /// constraint without expanding the internal factory surface.
    /// </para>
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        IServiceProvider services = this.HttpContext.RequestServices;
        ISlackInboundQueue inboundQueue = services.GetRequiredService<ISlackInboundQueue>();
        ILogger<SlackEventsController> logger = services.GetRequiredService<ILogger<SlackEventsController>>();
        SlackInboundEnvelopeFactory envelopeFactory = services.GetRequiredService<SlackInboundEnvelopeFactory>();
        ISlackInboundEnqueueDeadLetterSink deadLetter = services.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>();

        string body = await SlackInboundEnvelopeFactory
            .ReadBufferedBodyAsync(this.HttpContext, cancellationToken)
            .ConfigureAwait(false);

        if (this.LooksLikeUrlVerification(body))
        {
            SlackEventPayload payload = SlackInboundPayloadParser.ParseEvent(body);

            if (this.IsUrlVerification(payload))
            {
                logger.LogInformation(
                    "Slack Events API url_verification handshake answered with challenge length {ChallengeLength}.",
                    payload.Challenge!.Length);

                return this.Ok(new SlackUrlVerificationResponse(payload.Challenge!));
            }
        }

        SlackInboundEnvelope envelope = envelopeFactory.BuildEnvelope(
            SlackInboundSourceType.Event,
            body);

        // Register the enqueue as a post-response callback so a slow or
        // failing durable queue cannot delay Slack's 3-second ACK.
        // Implementation-plan.md §4.1 explicitly requires enqueue AFTER
        // ACK; OnCompleted fires once ASP.NET Core has flushed the
        // response, satisfying that ordering contract. The scheduler
        // retries the enqueue with bounded exponential backoff and
        // hands the envelope to the dead-letter sink on terminal
        // failure so events are not silently lost after the ACK.
        //
        // auditContext is intentionally null on the hot path: the
        // previous implementation passed payload.EventSubtype here,
        // which forced a second JSON parse upstream. The Slack
        // event sub-type is recoverable from envelope.RawPayload by
        // Stage 4.3's downstream ingestor, where the parse is no
        // longer on the 3-second ACK critical path.
        SlackInboundEnqueueScheduler.ScheduleAfterAck(
            this.HttpContext,
            inboundQueue,
            envelope,
            logger,
            deadLetter,
            auditContext: null);

        return this.Ok();
    }

    /// <summary>
    /// Cheap ordinal pre-filter that decides whether the request is
    /// worth parsing for a URL-verification handshake. Returns
    /// <see langword="true"/> when the body contains the
    /// <c>url_verification</c> discriminator OR the signature middleware
    /// already stamped the URL-verification marker on
    /// <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Correctness argument:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>The Slack Events API specification requires
    ///   URL-verification bodies to carry <c>"type":"url_verification"</c>
    ///   verbatim. The literal token <c>url_verification</c> is therefore
    ///   guaranteed to appear in any handshake body -- no false
    ///   negatives.</description></item>
    ///   <item><description>A regular <c>event_callback</c> body whose
    ///   user-supplied text happens to contain the literal
    ///   <c>url_verification</c> would yield a false positive, but the
    ///   subsequent strict <see cref="IsUrlVerification(SlackEventPayload)"/>
    ///   check restores correctness; the only cost is one wasted
    ///   <see cref="System.Text.Json.JsonDocument"/> allocation, which
    ///   is bounded by the (vanishingly small) collision
    ///   rate.</description></item>
    ///   <item><description>The signature middleware sets
    ///   <see cref="SlackSignatureValidator.UrlVerificationItemKey"/>
    ///   only when it has already confirmed the handshake shape, so
    ///   honouring that marker preserves the defense-in-depth
    ///   contract previously enforced inline.</description></item>
    /// </list>
    /// </remarks>
    private bool LooksLikeUrlVerification(string body)
    {
        if (!string.IsNullOrEmpty(body)
            && body.Contains(SlackInboundPayloadParser.UrlVerificationType, StringComparison.Ordinal))
        {
            return true;
        }

        if (this.HttpContext.Items.TryGetValue(SlackSignatureValidator.UrlVerificationItemKey, out object? marker)
            && marker is true)
        {
            return true;
        }

        return false;
    }

    private bool IsUrlVerification(SlackEventPayload payload)
    {
        if (payload.IsUrlVerification)
        {
            return true;
        }

        // Defense-in-depth: Stage 3.1's SlackSignatureValidator marks the
        // request via UrlVerificationItemKey only when the body itself is
        // a url_verification handshake. Honouring the marker here lets a
        // future test host that builds a synthetic verification request
        // (without exercising the signature middleware) still receive
        // the challenge contract.
        if (this.HttpContext.Items.TryGetValue(SlackSignatureValidator.UrlVerificationItemKey, out object? marker)
            && marker is true
            && !string.IsNullOrEmpty(payload.Challenge))
        {
            return true;
        }

        return false;
    }
}

/// <summary>
/// JSON response body for the Slack Events API URL-verification
/// handshake. Slack expects exactly one field: <c>challenge</c>.
/// </summary>
internal sealed record SlackUrlVerificationResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("challenge")] string Challenge);
