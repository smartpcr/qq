// -----------------------------------------------------------------------
// <copyright file="DefaultSlackModalFastPathHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 4.1 production-grade <see cref="ISlackModalFastPathHandler"/>:
/// runs the synchronous <c>auth + idempotency + views.open</c> pipeline
/// required by architecture.md §5.3 and tech-spec.md §5.2 for the
/// <c>/agent review</c> and <c>/agent escalate</c> sub-commands.
/// </summary>
/// <remarks>
/// <para>
/// The handler composes three pluggable collaborators:
/// </para>
/// <list type="number">
///   <item><description><see cref="SlackInProcessIdempotencyStore"/>
///   suppresses duplicate fast-path invocations within Slack's
///   <c>trigger_id</c> lifetime (Stage 4.3 replaces this with the
///   durable <c>SlackIdempotencyGuard</c>).</description></item>
///   <item><description><see cref="ISlackModalPayloadBuilder"/>
///   produces the Slack <c>view</c> JSON payload (Stage 5.2's
///   <c>SlackMessageRenderer</c> supersedes this).</description></item>
///   <item><description><see cref="ISlackViewsOpenClient"/> performs
///   the synchronous <c>views.open</c> Web API call (Stage 6.4's
///   <c>SlackDirectApiClient</c> supersedes this).</description></item>
/// </list>
/// <para>
/// The "auth" leg of the fast-path is enforced upstream by
/// <see cref="Security.SlackAuthorizationFilter"/> registered as a
/// global MVC filter in Stage 3.2: by the time the controller calls the
/// handler, the request has already passed the workspace, channel, and
/// user-group ACL checks. The handler therefore only owns the
/// idempotency + <c>views.open</c> legs.
/// </para>
/// <para>
/// Failure handling: if the idempotency reservation fails the handler
/// returns <see cref="SlackModalFastPathResult.DuplicateAck"/>; if the
/// <c>views.open</c> call fails or the workspace lacks a bot token
/// the handler RELEASES the idempotency token (so a retry can
/// succeed) and returns
/// <see cref="SlackModalFastPathResult.Handled(IActionResult)"/> with an
/// ephemeral error body. The handler NEVER returns
/// <see cref="SlackModalFastPathResult.AsyncFallback"/> because an
/// async-queued modal command is useless once <c>trigger_id</c> has
/// expired (architecture.md §5.3).
/// </para>
/// </remarks>
internal sealed class DefaultSlackModalFastPathHandler : ISlackModalFastPathHandler
{
    private readonly ISlackFastPathIdempotencyStore idempotencyStore;
    private readonly ISlackModalPayloadBuilder payloadBuilder;
    private readonly ISlackViewsOpenClient viewsOpenClient;
    private readonly SlackModalAuditRecorder auditRecorder;
    private readonly ILogger<DefaultSlackModalFastPathHandler> logger;

    public DefaultSlackModalFastPathHandler(
        ISlackFastPathIdempotencyStore idempotencyStore,
        ISlackModalPayloadBuilder payloadBuilder,
        ISlackViewsOpenClient viewsOpenClient,
        SlackModalAuditRecorder auditRecorder,
        ILogger<DefaultSlackModalFastPathHandler> logger)
    {
        this.idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        this.payloadBuilder = payloadBuilder ?? throw new ArgumentNullException(nameof(payloadBuilder));
        this.viewsOpenClient = viewsOpenClient ?? throw new ArgumentNullException(nameof(viewsOpenClient));
        this.auditRecorder = auditRecorder ?? throw new ArgumentNullException(nameof(auditRecorder));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SlackModalFastPathResult> HandleAsync(
        SlackInboundEnvelope envelope,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(httpContext);

        if (string.IsNullOrEmpty(envelope.TriggerId))
        {
            this.logger.LogWarning(
                "Slack modal fast-path rejected envelope idempotency_key={IdempotencyKey} team_id={TeamId}: missing trigger_id (cannot call views.open).",
                envelope.IdempotencyKey,
                envelope.TeamId);
            await this.auditRecorder
                .RecordErrorAsync(envelope, "unknown", "missing trigger_id; views.open cannot be called.", ct)
                .ConfigureAwait(false);
            return SlackModalFastPathResult.Handled(
                BuildEphemeralError("Could not open the modal: missing Slack trigger_id."));
        }

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(envelope.RawPayload);
        if (string.IsNullOrEmpty(payload.SubCommand))
        {
            this.logger.LogWarning(
                "Slack modal fast-path rejected envelope idempotency_key={IdempotencyKey} team_id={TeamId}: missing sub-command in text='{Text}'.",
                envelope.IdempotencyKey,
                envelope.TeamId,
                payload.Text);
            await this.auditRecorder
                .RecordErrorAsync(envelope, "unknown", "missing sub-command in command text.", ct)
                .ConfigureAwait(false);
            return SlackModalFastPathResult.Handled(
                BuildEphemeralError(
                    "Could not open the modal: missing sub-command (expected `/agent review …` or `/agent escalate …`)."));
        }

        SlackFastPathIdempotencyResult acquireResult = await this.idempotencyStore
            .TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: ct)
            .ConfigureAwait(false);
        if (acquireResult.IsDuplicate)
        {
            this.logger.LogInformation(
                "Slack modal fast-path detected duplicate idempotency_key={IdempotencyKey} for sub-command={SubCommand} team={TeamId} user={UserId} trigger_id={TriggerId} ({Diagnostic}); silent ACK.",
                envelope.IdempotencyKey,
                payload.SubCommand,
                envelope.TeamId,
                envelope.UserId,
                envelope.TriggerId,
                acquireResult.Diagnostic);
            await this.auditRecorder
                .RecordDuplicateAsync(envelope, payload.SubCommand!, acquireResult.Diagnostic ?? "duplicate", ct)
                .ConfigureAwait(false);
            return SlackModalFastPathResult.DuplicateAck;
        }

        if (acquireResult.Outcome == SlackFastPathIdempotencyOutcome.StoreUnavailable)
        {
            this.logger.LogWarning(
                "Slack modal fast-path proceeding with DEGRADED idempotency for key={IdempotencyKey} team={TeamId}: {Diagnostic}",
                envelope.IdempotencyKey,
                envelope.TeamId,
                acquireResult.Diagnostic);
        }

        object viewPayload;
        try
        {
            viewPayload = this.payloadBuilder.BuildView(payload.SubCommand!, envelope);
        }
        catch (Exception ex)
        {
            await this.idempotencyStore.ReleaseAsync(envelope.IdempotencyKey, ct).ConfigureAwait(false);
            this.logger.LogError(
                ex,
                "Slack modal payload builder failed for sub-command={SubCommand} team={TeamId} user={UserId}.",
                payload.SubCommand,
                envelope.TeamId,
                envelope.UserId);
            await this.auditRecorder
                .RecordErrorAsync(envelope, payload.SubCommand!, $"payload_builder_failed: {ex.Message}", ct)
                .ConfigureAwait(false);
            return SlackModalFastPathResult.Handled(
                BuildEphemeralError("Could not open the modal: failed to build the view payload."));
        }

        SlackViewsOpenResult viewsResult;
        try
        {
            viewsResult = await this.viewsOpenClient.OpenAsync(
                new SlackViewsOpenRequest(envelope.TeamId, envelope.TriggerId!, viewPayload),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await this.idempotencyStore.ReleaseAsync(envelope.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await this.idempotencyStore.ReleaseAsync(envelope.IdempotencyKey, ct).ConfigureAwait(false);
            this.logger.LogError(
                ex,
                "Slack modal fast-path views.open call threw for sub-command={SubCommand} team={TeamId} user={UserId}.",
                payload.SubCommand,
                envelope.TeamId,
                envelope.UserId);
            await this.auditRecorder
                .RecordErrorAsync(envelope, payload.SubCommand!, $"views_open_threw: {ex.GetType().Name} {ex.Message}", ct)
                .ConfigureAwait(false);
            return SlackModalFastPathResult.Handled(
                BuildEphemeralError("Could not open the modal: an unexpected error occurred talking to Slack."));
        }

        if (viewsResult.IsSuccess)
        {
            // Iter-4 fix: transition the durable idempotency row from
            // "reserved" to "modal_opened" so Stage 4.3's async ingestor
            // recognises this row as terminal and does NOT replay the
            // command through the async pipeline. Without this call the
            // row leaks at "reserved" forever (the EF backend exposes
            // MarkOpenedAsync but the iter-3 handler never invoked it),
            // defeating the cross-replica / cross-restart dedup contract
            // that justified pulling forward the durable backend.
            //
            // Iter-5 fix (evaluator item 2): pass CancellationToken.None
            // and wrap in try/catch so request-token cancellation cannot
            // surface as an OperationCanceledException AFTER the user has
            // already seen the modal. The interface contract
            // (ISlackFastPathIdempotencyStore.cs:101-103) explicitly
            // requires this call to be best-effort and non-throwing once
            // views.open has succeeded -- the modal exists in Slack's UI
            // and the caller cannot recover by retrying.
            try
            {
                await this.idempotencyStore
                    .MarkCompletedAsync(envelope.IdempotencyKey, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception markEx)
            {
                this.logger.LogWarning(
                    markEx,
                    "Slack modal fast-path failed to mark idempotency key={IdempotencyKey} as completed after a successful views.open. The user-visible modal is unaffected; the durable row will be reconciled by Stage 4.3's async ingestor on the next retry.",
                    envelope.IdempotencyKey);
            }

            this.logger.LogInformation(
                "Slack modal fast-path opened {SubCommand} modal for team={TeamId} user={UserId} trigger_id={TriggerId} idempotency_key={IdempotencyKey}.",
                payload.SubCommand,
                envelope.TeamId,
                envelope.UserId,
                envelope.TriggerId,
                envelope.IdempotencyKey);
            await this.auditRecorder
                .RecordSuccessAsync(envelope, payload.SubCommand!, ct)
                .ConfigureAwait(false);
            return SlackModalFastPathResult.Handled();
        }

        // views.open failed -- release the idempotency reservation so the
        // user can retry the command. Return an ephemeral error to the
        // invoking user; we deliberately do NOT enqueue the envelope for
        // async processing because the trigger_id is already expired (or
        // about to expire) and the orchestrator cannot make any progress
        // toward opening a modal once that happens (architecture.md §5.3).
        await this.idempotencyStore.ReleaseAsync(envelope.IdempotencyKey, ct).ConfigureAwait(false);
        string userMessage = viewsResult.Kind switch
        {
            SlackViewsOpenResultKind.MissingConfiguration =>
                "Could not open the modal: this Slack workspace is not configured for agent commands. Ask the admin to register the workspace.",
            SlackViewsOpenResultKind.NetworkFailure =>
                "Could not open the modal: Slack timed out or was unreachable. Please retry in a few seconds.",
            _ => $"Could not open the modal: Slack returned error '{viewsResult.Error ?? "unknown_error"}'.",
        };

        this.logger.LogWarning(
            "Slack modal fast-path views.open failed kind={Kind} error={Error} sub-command={SubCommand} team={TeamId} user={UserId} trigger_id={TriggerId}.",
            viewsResult.Kind,
            viewsResult.Error,
            payload.SubCommand,
            envelope.TeamId,
            envelope.UserId,
            envelope.TriggerId);

        await this.auditRecorder
            .RecordErrorAsync(
                envelope,
                payload.SubCommand!,
                $"views_open_{viewsResult.Kind}: {viewsResult.Error ?? "unknown"}",
                ct)
            .ConfigureAwait(false);

        return SlackModalFastPathResult.Handled(BuildEphemeralError(userMessage));
    }

    /// <summary>
    /// Builds the Slack-ephemeral response body. Slack renders the
    /// <c>text</c> only to the invoking user; the <c>response_type</c>
    /// of <c>ephemeral</c> is the contract.
    /// </summary>
    private static IActionResult BuildEphemeralError(string text)
        => new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = "application/json; charset=utf-8",
            Content = System.Text.Json.JsonSerializer.Serialize(new
            {
                response_type = "ephemeral",
                text,
            }),
        };
}
