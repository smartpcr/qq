// -----------------------------------------------------------------------
// <copyright file="SlackInteractionsController.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// ASP.NET Core controller hosting the Slack interactive endpoint
/// (<c>POST /api/slack/interactions</c>). Implements Stage 4.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// with the Stage 5.3 synchronous fast-path layered on top
/// (architecture.md §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Slack POSTs every Block Kit button click, <c>view_submission</c>, and
/// other interactive payload as <c>application/x-www-form-urlencoded</c>
/// with the JSON document hidden inside the <c>payload</c> form field.
/// The controller normalises the wrapped payload into a
/// <see cref="SlackInboundEnvelope"/>, runs the synchronous
/// <see cref="ISlackInteractionFastPathHandler"/>, and -- when the
/// fast-path returns <see cref="SlackInteractionFastPathResultKind.AsyncFallback"/>
/// -- enqueues the envelope onto <see cref="ISlackInboundQueue"/> after
/// the ACK has flushed. Stage 5.3's <c>SlackInteractionHandler</c> turns
/// the envelope into a <c>HumanDecisionEvent</c> off the request thread.
/// </para>
/// <para>
/// Why the fast-path. Button clicks whose backing
/// <c>HumanAction.RequiresComment = true</c> need to open a follow-up
/// comment modal via <c>views.open</c>; Slack's <c>trigger_id</c>
/// expires within approximately three seconds of issuance, so the
/// async path (which intentionally defers the enqueue until AFTER the
/// ACK) cannot meet the deadline. The fast-path opens the modal
/// inline; on success it returns
/// <see cref="SlackInteractionFastPathResultKind.Handled"/> and the
/// controller skips the enqueue (the user's eventual modal submission
/// arrives as a separate inbound envelope, which the async handler
/// converts into the <c>HumanDecisionEvent</c>).
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
    /// <see cref="ISlackInboundQueue"/>,
    /// <see cref="ISlackInteractionFastPathHandler"/>) are resolved from
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
        ISlackInteractionFastPathHandler fastPath = services.GetRequiredService<ISlackInteractionFastPathHandler>();

        string body = await SlackInboundEnvelopeFactory
            .ReadBufferedBodyAsync(this.HttpContext, cancellationToken)
            .ConfigureAwait(false);

        SlackInboundEnvelope envelope = envelopeFactory.BuildEnvelope(
            SlackInboundSourceType.Interaction,
            body);

        SlackInteractionPayload payload = SlackInboundPayloadParser.ParseInteraction(body);

        // Run the synchronous interaction fast-path BEFORE the ACK so
        // any trigger_id-bound side-effect (currently: opening a
        // RequiresComment follow-up modal via views.open) lands while
        // the trigger_id is still valid. The fast-path returns
        // AsyncFallback for everything it does not own, so the
        // ordinary post-ACK enqueue path runs unchanged for plain
        // button clicks and modal submissions.
        SlackInteractionFastPathResult fastPathResult = await fastPath
            .HandleAsync(envelope, this.HttpContext, cancellationToken)
            .ConfigureAwait(false);

        if (fastPathResult.ResultKind == SlackInteractionFastPathResultKind.Handled)
        {
            this.logger.LogInformation(
                "Slack interaction fast-path handled interaction_type={InteractionType} action_or_view_id={ActionOrViewId} team={TeamId} user={UserId} trigger_id={TriggerId}; skipping async enqueue.",
                payload.Type,
                payload.ActionOrViewId,
                envelope.TeamId,
                envelope.UserId,
                envelope.TriggerId);
            return fastPathResult.ActionResult ?? this.Ok();
        }

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
