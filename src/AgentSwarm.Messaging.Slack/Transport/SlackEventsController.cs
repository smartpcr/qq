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

        SlackEventPayload payload = SlackInboundPayloadParser.ParseEvent(body);

        if (this.IsUrlVerification(payload))
        {
            logger.LogInformation(
                "Slack Events API url_verification handshake answered with challenge length {ChallengeLength}.",
                payload.Challenge!.Length);

            return this.Ok(new SlackUrlVerificationResponse(payload.Challenge!));
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
        SlackInboundEnqueueScheduler.ScheduleAfterAck(
            this.HttpContext,
            inboundQueue,
            envelope,
            logger,
            deadLetter,
            payload.EventSubtype);

        return this.Ok();
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
