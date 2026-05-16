// -----------------------------------------------------------------------
// <copyright file="SlackInteractionsController.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// ASP.NET Core controller hosting the Slack interactive endpoint
/// (<c>POST /api/slack/interactions</c>). Implements Stage 4.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Slack POSTs every Block Kit button click, <c>view_submission</c>, and
/// other interactive payload as <c>application/x-www-form-urlencoded</c>
/// with the JSON document hidden inside the <c>payload</c> form field.
/// The controller normalises the wrapped payload into a
/// <see cref="SlackInboundEnvelope"/>, enqueues it onto
/// <see cref="ISlackInboundQueue"/>, and immediately returns HTTP 200
/// to satisfy Slack's 3-second ACK budget. Stage 5.3's
/// <c>SlackInteractionHandler</c> turns the envelope into a
/// <c>HumanDecisionEvent</c> off the request thread.
/// </para>
/// </remarks>
[ApiController]
[Route("api/slack/interactions")]
[Consumes("application/x-www-form-urlencoded")]
public sealed class SlackInteractionsController : ControllerBase
{
    private readonly ILogger<SlackInteractionsController> logger;

    /// <summary>
    /// Initializes a new instance bound to the supplied logger. The
    /// internal dependencies
    /// (<see cref="SlackInboundEnvelopeFactory"/>,
    /// <see cref="ISlackInboundQueue"/>) are resolved from
    /// <see cref="Microsoft.AspNetCore.Http.HttpContext.RequestServices"/>
    /// inside the action method because the brief pins those types as
    /// <c>internal</c> and MVC requires the controller class to be
    /// public.
    /// </summary>
    public SlackInteractionsController(ILogger<SlackInteractionsController> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Accepts a Slack interactive POST. Returns HTTP 200 within the
    /// 3-second ACK budget.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        IServiceProvider services = this.HttpContext.RequestServices;
        ISlackInboundQueue inboundQueue = services.GetRequiredService<ISlackInboundQueue>();
        SlackInboundEnvelopeFactory envelopeFactory = services.GetRequiredService<SlackInboundEnvelopeFactory>();
        ISlackInboundEnqueueDeadLetterSink deadLetter = services.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>();

        string body = await SlackInboundEnvelopeFactory
            .ReadBufferedBodyAsync(this.HttpContext, cancellationToken)
            .ConfigureAwait(false);

        SlackInboundEnvelope envelope = envelopeFactory.BuildEnvelope(
            SlackInboundSourceType.Interaction,
            body);

        SlackInteractionPayload payload = SlackInboundPayloadParser.ParseInteraction(body);

        // Defer the enqueue until AFTER the ACK is flushed so a slow or
        // failing queue cannot delay Slack's 3-second budget.
        SlackInboundEnqueueScheduler.ScheduleAfterAck(
            this.HttpContext,
            inboundQueue,
            envelope,
            this.logger,
            deadLetter,
            $"interaction_type={payload.Type} action_or_view_id={payload.ActionOrViewId}");

        return this.Ok();
    }
}
