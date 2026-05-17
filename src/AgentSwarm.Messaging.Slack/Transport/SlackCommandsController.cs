// -----------------------------------------------------------------------
// <copyright file="SlackCommandsController.cs" company="Microsoft Corp.">
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
/// ASP.NET Core controller hosting the Slack slash command endpoint
/// (<c>POST /api/slack/commands</c>). Implements Stage 4.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint handles both async and synchronous flows per
/// architecture.md §2.2.2 and §5.3:
/// </para>
/// <list type="number">
///   <item><description><b>Async path (default).</b> Normalises the form
///   payload into a <see cref="SlackInboundEnvelope"/>, enqueues it onto
///   <see cref="ISlackInboundQueue"/>, and immediately returns HTTP 200.
///   Subsequent handler dispatch (Stage 4.3 +
///   Stage 5.1) runs out-of-band so Slack's 3-second ACK budget is
///   met regardless of orchestrator latency.</description></item>
///   <item><description><b>Synchronous modal fast-path.</b> For the
///   <c>review</c> and <c>escalate</c> sub-commands, the controller
///   delegates to <see cref="ISlackModalFastPathHandler"/> which runs
///   the idempotency check and <c>views.open</c> Web API call inline
///   -- Slack's <c>trigger_id</c> expires within approximately three
///   seconds (tech-spec.md §5.2), so deferring the modal-open to the
///   async queue would always miss the deadline. The
///   <see cref="Security.SlackAuthorizationFilter"/> registered as a
///   global MVC filter satisfies the "auth" leg of the fast-path
///   transparently; the fast-path handler owns the
///   idempotency + <c>views.open</c> legs.</description></item>
/// </list>
/// </remarks>
[ApiController]
[Route("api/slack/commands")]
[Consumes("application/x-www-form-urlencoded")]
public sealed class SlackCommandsController : ControllerBase
{
    /// <summary>
    /// Slash-command sub-command token (case-insensitive) that opens
    /// the code-review modal via the fast-path. Mirrors
    /// architecture.md §5.3.
    /// </summary>
    public const string ReviewSubCommand = "review";

    /// <summary>
    /// Slash-command sub-command token (case-insensitive) that opens
    /// the escalation modal via the fast-path. Mirrors
    /// architecture.md §5.3.
    /// </summary>
    public const string EscalateSubCommand = "escalate";

    private readonly ILogger<SlackCommandsController> logger;

    /// <summary>
    /// Initializes a new instance bound to the supplied logger. All
    /// other dependencies
    /// (<see cref="SlackInboundEnvelopeFactory"/>,
    /// <see cref="ISlackInboundQueue"/>,
    /// <see cref="ISlackModalFastPathHandler"/>) are resolved from
    /// <see cref="Microsoft.AspNetCore.Http.HttpContext.RequestServices"/>
    /// inside the action method because the brief pins those types as
    /// <c>internal</c> (Stage 1.3 / Stage 4.1) and MVC requires the
    /// controller class to be public.
    /// </summary>
    public SlackCommandsController(ILogger<SlackCommandsController> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Accepts a Slack slash-command POST. Returns HTTP 200 within the
    /// 3-second ACK budget. Modal-opening sub-commands invoke the
    /// synchronous fast-path; all other sub-commands are enqueued for
    /// async processing.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        IServiceProvider services = this.HttpContext.RequestServices;
        ISlackInboundQueue inboundQueue = services.GetRequiredService<ISlackInboundQueue>();
        ISlackModalFastPathHandler modalFastPathHandler = services.GetRequiredService<ISlackModalFastPathHandler>();
        SlackInboundEnvelopeFactory envelopeFactory = services.GetRequiredService<SlackInboundEnvelopeFactory>();
        ISlackInboundEnqueueDeadLetterSink deadLetter = services.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>();

        string body = await SlackInboundEnvelopeFactory
            .ReadBufferedBodyAsync(this.HttpContext, cancellationToken)
            .ConfigureAwait(false);

        SlackInboundEnvelope envelope = envelopeFactory.BuildEnvelope(
            SlackInboundSourceType.Command,
            body);

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(body);

        if (IsModalFastPathSubCommand(payload.SubCommand))
        {
            SlackModalFastPathResult result = await modalFastPathHandler
                .HandleAsync(envelope, this.HttpContext, cancellationToken)
                .ConfigureAwait(false);

            switch (result.ResultKind)
            {
                case SlackModalFastPathResultKind.Handled:
                    this.logger.LogInformation(
                        "Slack modal fast-path handled {SubCommand} for team={TeamId} user={UserId} trigger_id={TriggerId}.",
                        payload.SubCommand,
                        envelope.TeamId,
                        envelope.UserId,
                        envelope.TriggerId);
                    return result.ActionResult ?? this.Ok();

                case SlackModalFastPathResultKind.DuplicateAck:
                    this.logger.LogInformation(
                        "Slack modal fast-path detected duplicate {SubCommand} for team={TeamId} user={UserId} trigger_id={TriggerId}; silent ACK.",
                        payload.SubCommand,
                        envelope.TeamId,
                        envelope.UserId,
                        envelope.TriggerId);
                    return this.Ok();

                case SlackModalFastPathResultKind.AsyncFallback:
                default:
                    // The modal fast-path MUST NOT fall through to the
                    // async enqueue path: by the time the orchestrator
                    // would dequeue the envelope, Slack's trigger_id has
                    // already expired (architecture.md §5.3) and the
                    // views.open call can no longer succeed. Tell the
                    // user the modal could not be opened so they can
                    // retry, and skip the enqueue entirely.
                    this.logger.LogWarning(
                        "Slack modal fast-path returned AsyncFallback for {SubCommand} (team={TeamId} user={UserId}); refusing to enqueue because trigger_id={TriggerId} will expire before the async path can run. Register a real ISlackModalFastPathHandler.",
                        payload.SubCommand,
                        envelope.TeamId,
                        envelope.UserId,
                        envelope.TriggerId);
                    return BuildModalFallbackError(payload.SubCommand!);
            }
        }

        // Async enqueue path: register the queue write as a
        // post-response callback so a slow or failing queue cannot
        // delay Slack's 3-second ACK.
        SlackInboundEnqueueScheduler.ScheduleAfterAck(
            this.HttpContext,
            inboundQueue,
            envelope,
            this.logger,
            deadLetter,
            $"command={payload.Command} sub_command={payload.SubCommand}");

        return this.Ok();
    }

    private static IActionResult BuildModalFallbackError(string subCommand)
        => new ContentResult
        {
            StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status200OK,
            ContentType = "application/json; charset=utf-8",
            Content = System.Text.Json.JsonSerializer.Serialize(new
            {
                response_type = "ephemeral",
                text = $"Could not open the `{subCommand}` modal: the Slack modal handler is not configured on this host. Please contact an administrator.",
            }),
        };

    private static bool IsModalFastPathSubCommand(string? subCommand)
    {
        if (string.IsNullOrEmpty(subCommand))
        {
            return false;
        }

        return string.Equals(subCommand, ReviewSubCommand, StringComparison.OrdinalIgnoreCase)
            || string.Equals(subCommand, EscalateSubCommand, StringComparison.OrdinalIgnoreCase);
    }
}
