// -----------------------------------------------------------------------
// <copyright file="DefaultSlackInteractionFastPathHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Production <see cref="ISlackInteractionFastPathHandler"/>. Opens
/// the comment modal inline (BEFORE the controller flushes the ACK)
/// for Block Kit button clicks whose <c>block_id</c> encodes a
/// <c>RequiresComment = true</c> action -- the only interactive
/// payload kind whose side-effect is racing Slack's ~3-second
/// <c>trigger_id</c> expiry.
/// </summary>
/// <remarks>
/// <para>
/// Behaviour summary:
/// </para>
/// <list type="bullet">
///   <item><description>Non-<c>block_actions</c> payloads (incl. modal
///   submissions and shortcuts) return
///   <see cref="SlackInteractionFastPathResult.AsyncFallback"/>; the
///   controller continues with the post-ACK enqueue path so the async
///   <see cref="SlackInteractionHandler"/> processes them.</description></item>
///   <item><description><c>block_actions</c> payloads whose
///   <c>block_id</c> does NOT decode to a <c>qc:</c>-prefixed question
///   block also return <see cref="SlackInteractionFastPathResult.AsyncFallback"/>
///   (the click is a normal decision that does not need an
///   inline <c>views.open</c>).</description></item>
///   <item><description><c>block_actions</c> payloads whose
///   <c>block_id</c> decodes to <c>RequiresComment = true</c> open
///   the comment modal INLINE via <see cref="ISlackViewsOpenClient"/>.
///   On success the fast-path returns
///   <see cref="SlackInteractionFastPathResult.Handled()"/> and the
///   controller short-circuits the enqueue (the eventual
///   view_submission produces its own envelope, which the async
///   handler converts into the <see cref="HumanDecisionEvent"/>).</description></item>
/// </list>
/// <para>
/// Failure handling. <c>views.open</c> failures, missing
/// <c>trigger_id</c>, and renderer exceptions return
/// <see cref="SlackInteractionFastPathResult.Handled(IActionResult)"/>
/// with an ephemeral error body so the user sees a Slack-rendered
/// message explaining what went wrong; the envelope is NOT enqueued
/// because the trigger_id is already expired or about to expire and
/// the async path cannot make any forward progress on
/// <c>views.open</c> (architecture.md §5.3, mirrors
/// <see cref="DefaultSlackModalFastPathHandler"/>'s contract).
/// </para>
/// </remarks>
internal sealed class DefaultSlackInteractionFastPathHandler : ISlackInteractionFastPathHandler
{
    /// <summary>
    /// Stable <c>sub_command</c> label stamped on every
    /// <see cref="SlackModalAuditRecorder"/> row this fast-path emits.
    /// The slash-command modal handler uses the parsed slash-command
    /// sub-command (e.g., <c>review</c>, <c>escalate</c>) for the same
    /// field; the interaction fast-path has no slash command -- the
    /// click is always opening a comment modal in response to a
    /// RequiresComment button -- so we pin a synthetic name so audit
    /// rows are filterable in operator queries.
    /// </summary>
    public const string AuditSubCommand = "comment_modal";

    private readonly ISlackViewsOpenClient viewsOpenClient;
    private readonly ISlackMessageRenderer messageRenderer;
    private readonly ISlackThreadMappingLookup threadMappingLookup;
    private readonly ISlackFastPathIdempotencyStore idempotencyStore;
    private readonly SlackModalAuditRecorder auditRecorder;
    private readonly ILogger<DefaultSlackInteractionFastPathHandler> logger;

    public DefaultSlackInteractionFastPathHandler(
        ISlackViewsOpenClient viewsOpenClient,
        ISlackMessageRenderer messageRenderer,
        ISlackThreadMappingLookup threadMappingLookup,
        ISlackFastPathIdempotencyStore idempotencyStore,
        SlackModalAuditRecorder auditRecorder,
        ILogger<DefaultSlackInteractionFastPathHandler> logger)
    {
        this.viewsOpenClient = viewsOpenClient ?? throw new ArgumentNullException(nameof(viewsOpenClient));
        this.messageRenderer = messageRenderer ?? throw new ArgumentNullException(nameof(messageRenderer));
        this.threadMappingLookup = threadMappingLookup ?? throw new ArgumentNullException(nameof(threadMappingLookup));
        this.idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        this.auditRecorder = auditRecorder ?? throw new ArgumentNullException(nameof(auditRecorder));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SlackInteractionFastPathResult> HandleAsync(
        SlackInboundEnvelope envelope,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(httpContext);

        SlackInteractionDetail? detail = SlackInteractionPayloadDetailParser.TryParse(envelope.RawPayload);
        if (detail is null || !string.Equals(detail.Type, SlackInteractionHandler.BlockActionsType, StringComparison.Ordinal))
        {
            // Not a block_actions payload (or unparseable) -- defer
            // to the async path. view_submission / shortcut payloads
            // are not trigger_id-bound on the inbound side; the async
            // handler processes them.
            return SlackInteractionFastPathResult.AsyncFallback;
        }

        SlackInteractionAction? action = detail.PrimaryAction;
        if (action is null || string.IsNullOrEmpty(action.BlockId))
        {
            return SlackInteractionFastPathResult.AsyncFallback;
        }

        if (!SlackInteractionEncoding.TryDecodeQuestionBlockId(
                action.BlockId,
                out string questionId,
                out bool requiresComment))
        {
            // Unknown / non-question block_id -- not the fast-path's
            // job. The async handler logs and discards.
            return SlackInteractionFastPathResult.AsyncFallback;
        }

        if (!requiresComment)
        {
            // Plain "approve / reject" style click -- the async path
            // publishes the HumanDecisionEvent and updates the message
            // via chat.update. No trigger_id race here because the
            // decision is the user-visible side-effect, not a modal.
            return SlackInteractionFastPathResult.AsyncFallback;
        }

        string? triggerId = detail.TriggerId ?? envelope.TriggerId;
        if (string.IsNullOrEmpty(triggerId))
        {
            this.logger.LogWarning(
                "SlackInteractionFastPath cannot open comment modal: missing trigger_id on envelope idempotency_key={IdempotencyKey} question_id={QuestionId}.",
                envelope.IdempotencyKey,
                questionId);
            // Every terminal of the fast-path emits a
            // SlackModalAuditRecorder row so the audit log captures the
            // same observability surface the slash-command modal fast-path
            // provides (DefaultSlackModalFastPathHandler). Without these
            // the RequiresComment HTTP path that short-circuits the async
            // pipeline is invisible to audit-driven dashboards / alerts.
            await this.auditRecorder
                .RecordErrorAsync(envelope, AuditSubCommand, "missing trigger_id; views.open cannot be called.", ct)
                .ConfigureAwait(false);
            return SlackInteractionFastPathResult.Handled(
                BuildEphemeralError(
                    "Could not open the comment dialog: missing Slack trigger_id. Please click the button again."));
        }

        // Reserve the envelope's idempotency key BEFORE any
        // user-visible side effect (views.open, DB lookup, renderer).
        // Slack retries the same button click up to three times if the
        // HTTP ACK is not delivered within ~3 seconds, and each retry
        // carries the same trigger_id + action_id, which produces the
        // same envelope.IdempotencyKey via SlackInboundEnvelopeFactory's
        // `interact:{team}:{user}:{action_or_view_id}:{trigger_id}`
        // formula. Without this guard a retry can open a second comment
        // modal AND the user's submission of either modal can produce
        // a separate HumanDecisionEvent through the async pipeline.
        // The pattern mirrors DefaultSlackModalFastPathHandler:
        //   * Acquired -> proceed
        //   * Duplicate -> silent ACK with Handled(); the original
        //     attempt already owns the click
        //   * StoreUnavailable -> log and proceed degraded (the
        //     in-process L1 still gates same-process retries)
        SlackFastPathIdempotencyResult acquireResult = await this.idempotencyStore
            .TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: ct)
            .ConfigureAwait(false);
        if (acquireResult.IsDuplicate)
        {
            this.logger.LogInformation(
                "SlackInteractionFastPath detected duplicate idempotency_key={IdempotencyKey} for question_id={QuestionId} team={TeamId} user={UserId} trigger_id={TriggerId} ({Diagnostic}); silent ACK without re-opening the comment modal.",
                envelope.IdempotencyKey,
                questionId,
                envelope.TeamId,
                envelope.UserId,
                triggerId,
                acquireResult.Diagnostic);
            // Record the duplicate outcome so operators can correlate
            // Slack-side retries with this fast-path's silent ACKs.
            await this.auditRecorder
                .RecordDuplicateAsync(envelope, AuditSubCommand, acquireResult.Diagnostic ?? "duplicate", ct)
                .ConfigureAwait(false);
            return SlackInteractionFastPathResult.Handled();
        }

        if (acquireResult.Outcome == SlackFastPathIdempotencyOutcome.StoreUnavailable)
        {
            this.logger.LogWarning(
                "SlackInteractionFastPath proceeding with DEGRADED idempotency for key={IdempotencyKey} team={TeamId}: {Diagnostic}",
                envelope.IdempotencyKey,
                envelope.TeamId,
                acquireResult.Diagnostic);
        }

        string fallbackCorrelationId = string.IsNullOrEmpty(envelope.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : envelope.IdempotencyKey;

        string? lookupKey = !string.IsNullOrEmpty(detail.ThreadTs)
            ? detail.ThreadTs
            : detail.MessageTs;

        // Do NOT catch thread-mapping lookup failures and pin the
        // fallback idempotency key into the modal's private_metadata.
        // The async SlackInteractionHandler trusts the pinned
        // correlation id when it processes the eventual
        // view_submission, so silently falling back here would publish
        // the user's RequiresComment decision under the WRONG
        // correlation id when the DB is down -- the same hazard the
        // async path already addresses. Instead, surface an ephemeral
        // error so the user retries when the lookup recovers. The
        // trigger_id is wasted but no wrong-correlation decision is
        // published.
        string correlationId;
        try
        {
            correlationId = await this
                .ResolveCorrelationIdAsync(envelope, detail.ChannelId, lookupKey, fallbackCorrelationId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Best-effort release on cancellation so a retry following
            // the cancel can acquire a fresh reservation.
            await this.idempotencyStore.ReleaseAsync(envelope.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await this.idempotencyStore.ReleaseAsync(envelope.IdempotencyKey, ct).ConfigureAwait(false);
            this.logger.LogError(
                ex,
                "SlackInteractionFastPath thread mapping lookup threw for envelope idempotency_key={IdempotencyKey} team={TeamId} channel={ChannelId} thread_ts={ThreadTs}; refusing to open the comment modal with a fallback correlation id and surfacing an ephemeral error so the user retries once the lookup recovers.",
                envelope.IdempotencyKey,
                envelope.TeamId,
                detail.ChannelId,
                lookupKey);
            await this.auditRecorder
                .RecordErrorAsync(
                    envelope,
                    AuditSubCommand,
                    $"thread_mapping_lookup_failed: {ex.GetType().Name} {ex.Message}",
                    ct)
                .ConfigureAwait(false);
            return SlackInteractionFastPathResult.Handled(
                BuildEphemeralError(
                    "Could not open the comment dialog: failed to resolve the task context. Please retry in a few seconds."));
        }

        SlackCommentModalContext modalContext = new(
            QuestionId: questionId,
            ActionValue: action.Value ?? string.Empty,
            ActionLabel: action.Label ?? action.Value ?? string.Empty,
            TeamId: envelope.TeamId,
            ChannelId: detail.ChannelId ?? envelope.ChannelId,
            MessageTs: detail.MessageTs ?? string.Empty,
            ThreadTs: detail.ThreadTs,
            UserId: detail.UserId ?? envelope.UserId,
            CorrelationId: correlationId);

        object viewPayload;
        try
        {
            viewPayload = this.messageRenderer.RenderCommentModal(modalContext);
        }
        catch (Exception ex)
        {
            await this.idempotencyStore.ReleaseAsync(envelope.IdempotencyKey, ct).ConfigureAwait(false);
            this.logger.LogError(
                ex,
                "SlackInteractionFastPath comment-modal renderer threw for envelope idempotency_key={IdempotencyKey} question_id={QuestionId}; surfacing an ephemeral error to the user.",
                envelope.IdempotencyKey,
                questionId);
            await this.auditRecorder
                .RecordErrorAsync(
                    envelope,
                    AuditSubCommand,
                    $"renderer_failed: {ex.GetType().Name} {ex.Message}",
                    ct)
                .ConfigureAwait(false);
            return SlackInteractionFastPathResult.Handled(
                BuildEphemeralError(
                    "Could not open the comment dialog: failed to build the view payload."));
        }

        SlackViewsOpenResult result;
        try
        {
            result = await this.viewsOpenClient
                .OpenAsync(new SlackViewsOpenRequest(envelope.TeamId, triggerId, viewPayload), ct)
                .ConfigureAwait(false);
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
                "SlackInteractionFastPath views.open threw for envelope idempotency_key={IdempotencyKey} question_id={QuestionId} trigger_id={TriggerId}.",
                envelope.IdempotencyKey,
                questionId,
                triggerId);
            await this.auditRecorder
                .RecordErrorAsync(
                    envelope,
                    AuditSubCommand,
                    $"views_open_threw: {ex.GetType().Name} {ex.Message}",
                    ct)
                .ConfigureAwait(false);
            return SlackInteractionFastPathResult.Handled(
                BuildEphemeralError(
                    "Could not open the comment dialog: an unexpected error occurred talking to Slack. Please retry."));
        }

        if (result.IsSuccess)
        {
            // Mirror DefaultSlackModalFastPathHandler: best-effort
            // MarkCompletedAsync with CancellationToken.None (the
            // user-visible modal has already opened, so request-token
            // cancellation must not surface as an OperationCanceledException
            // here; the interface contract requires this call to be
            // non-throwing once views.open has succeeded).
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
                    "SlackInteractionFastPath failed to mark idempotency key={IdempotencyKey} as completed after a successful views.open. The user-visible modal is unaffected; the durable row will be reconciled on the next retry.",
                    envelope.IdempotencyKey);
            }

            this.logger.LogInformation(
                "SlackInteractionFastPath opened comment modal inline for question_id={QuestionId} team={TeamId} user={UserId} trigger_id={TriggerId} idempotency_key={IdempotencyKey}.",
                questionId,
                envelope.TeamId,
                detail.UserId,
                triggerId,
                envelope.IdempotencyKey);

            // The audit row write is a best-effort durable side-effect
            // that runs AFTER the user-visible modal has opened. Forward
            // CancellationToken.None and wrap in try/catch so a dropped
            // HTTP connection cannot leak an OperationCanceledException
            // and convert a user-successful request into a 5xx -- a
            // missing audit row is recoverable via the structured log
            // line above, a 5xx is not.
            //
            // Stage 7.1 evaluator iter-1 item 3: stamp the serialised
            // comment-modal view JSON onto the audit row's
            // ResponsePayload so successful interactions persist the
            // story-required "response payload" field, not just errors.
            try
            {
                string? serialisedView = SlackAuditPayloadSerializer.Serialize(viewPayload);
                await this.auditRecorder
                    .RecordSuccessAsync(envelope, AuditSubCommand, CancellationToken.None, serialisedView)
                    .ConfigureAwait(false);
            }
            catch (Exception auditEx)
            {
                this.logger.LogWarning(
                    auditEx,
                    "SlackInteractionFastPath failed to record the modal_open success audit row for idempotency_key={IdempotencyKey} after a successful views.open. The user-visible modal is unaffected; the structured log line above is the fallback observability surface.",
                    envelope.IdempotencyKey);
            }

            return SlackInteractionFastPathResult.Handled();
        }

        await this.idempotencyStore.ReleaseAsync(envelope.IdempotencyKey, ct).ConfigureAwait(false);
        this.logger.LogWarning(
            "SlackInteractionFastPath views.open failed kind={Kind} error={Error} question_id={QuestionId} team={TeamId} trigger_id={TriggerId}; surfacing an ephemeral error.",
            result.Kind,
            result.Error,
            questionId,
            envelope.TeamId,
            triggerId);
        string userMessage = result.Kind switch
        {
            SlackViewsOpenResultKind.MissingConfiguration =>
                "Could not open the comment dialog: this Slack workspace is not configured for agent interactions. Ask the admin to register the workspace.",
            SlackViewsOpenResultKind.NetworkFailure =>
                "Could not open the comment dialog: Slack timed out or was unreachable. Please retry in a few seconds.",
            _ => $"Could not open the comment dialog: Slack returned error '{result.Error ?? "unknown_error"}'.",
        };
        await this.auditRecorder
            .RecordErrorAsync(
                envelope,
                AuditSubCommand,
                $"views_open_{result.Kind}: {result.Error ?? "unknown"}",
                ct)
            .ConfigureAwait(false);
        return SlackInteractionFastPathResult.Handled(BuildEphemeralError(userMessage));
    }

    private async Task<string> ResolveCorrelationIdAsync(
        SlackInboundEnvelope envelope,
        string? channelId,
        string? threadTs,
        string fallback,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelope.TeamId)
            || string.IsNullOrEmpty(channelId)
            || string.IsNullOrEmpty(threadTs))
        {
            return fallback;
        }

        // The lookup MUST NOT catch and swallow exceptions: a silent
        // fallback to the envelope's idempotency key would let
        // HandleAsync pin the wrong correlation id into the comment
        // modal's private_metadata, which SlackInteractionHandler then
        // trusts for the eventual view_submission. Exceptions bubble
        // to the call site, which surfaces an ephemeral error instead
        // of opening a modal that would publish a misrouted decision.
        SlackThreadMapping? mapping = await this.threadMappingLookup
            .LookupAsync(envelope.TeamId, channelId, threadTs, ct)
            .ConfigureAwait(false);
        if (mapping is not null && !string.IsNullOrEmpty(mapping.CorrelationId))
        {
            return mapping.CorrelationId;
        }

        return fallback;
    }

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

/// <summary>
/// No-op <see cref="ISlackInteractionFastPathHandler"/> registered as
/// the default by
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddSlackInboundTransport(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
/// so the <see cref="SlackInteractionsController"/> can always
/// resolve a fast-path. Hosts that opt into the Stage 5.3 dispatcher
/// via <see cref="SlackInteractionDispatchServiceCollectionExtensions.AddSlackInteractionDispatcher"/>
/// replace this default with
/// <see cref="DefaultSlackInteractionFastPathHandler"/>.
/// </summary>
internal sealed class NoOpSlackInteractionFastPathHandler : ISlackInteractionFastPathHandler
{
    /// <inheritdoc />
    public Task<SlackInteractionFastPathResult> HandleAsync(
        SlackInboundEnvelope envelope,
        HttpContext httpContext,
        CancellationToken ct)
    {
        // Always defer to the async path. The Stage 4.1 controller
        // works exactly as it did before Stage 5.3 wired in the real
        // fast-path: it post-ACK enqueues every envelope. Hosts that
        // wire the real DefaultSlackInteractionFastPathHandler get
        // the inline views.open behaviour.
        return Task.FromResult(SlackInteractionFastPathResult.AsyncFallback);
    }
}
