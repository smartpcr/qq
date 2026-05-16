// -----------------------------------------------------------------------
// <copyright file="SlackInteractionHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 5.3 production <see cref="ISlackInteractionHandler"/>. Decodes
/// Slack Block Kit button clicks and modal <c>view_submission</c>
/// payloads, maps them to typed
/// <see cref="HumanDecisionEvent"/>s, publishes the events through
/// <see cref="IAgentTaskService.PublishDecisionAsync"/>, and -- for
/// button clicks -- updates the originating Slack message via
/// <c>chat.update</c> to disable the buttons and reflect the decision.
/// </summary>
/// <remarks>
/// <para>
/// Architecture.md §2.9 owns the mapping table. The brief breaks the
/// stage into eight implementation steps that this class covers:
/// </para>
/// <list type="number">
///   <item><description>Process button-click and modal-submission payloads (§2.9).</description></item>
///   <item><description>Extract <c>action_id</c>, <c>value</c>, <c>user.id</c>, <c>message.ts</c>, <c>trigger_id</c> from button payloads; decode <c>QuestionId</c> from the button's <c>block_id</c> via <see cref="SlackInteractionEncoding"/> (§5.2 lines 629-634).</description></item>
///   <item><description>Parse <c>view.state.values</c> for modal submissions; pull <c>QuestionId</c> from <c>view.private_metadata</c>; map verdict and text input to <c>ActionValue</c> / <c>Comment</c>.</description></item>
///   <item><description>Resolve <c>CorrelationId</c> from <see cref="SlackThreadMapping"/> via the parent message's <c>thread_ts</c>; fall back to the envelope's idempotency key when no mapping exists.</description></item>
///   <item><description>Populate the canonical fields: <c>Messenger = "slack"</c>, <c>ExternalUserId</c>, <c>ExternalMessageId</c>, <c>ReceivedAt</c>.</description></item>
///   <item><description>Publish the <c>HumanDecisionEvent</c>.</description></item>
///   <item><description>Issue <c>chat.update</c> to disable the original buttons (button-click flow only -- modal submissions have no anchored parent message to mutate).</description></item>
///   <item><description>Open a follow-up comment modal via <c>views.open</c> when the clicked button's encoded <see cref="HumanAction.RequiresComment"/> flag is true; the subsequent <c>view_submission</c> then completes the decision.</description></item>
/// </list>
/// <para>
/// Failure semantics. Any exception thrown by
/// <see cref="IAgentTaskService.PublishDecisionAsync"/> propagates out
/// of <see cref="HandleAsync"/> so the
/// <see cref="SlackInboundProcessingPipeline"/> can retry / dead-letter
/// the envelope (matches <see cref="SlackCommandHandler"/>'s contract).
/// Failures inside the best-effort <c>chat.update</c> and
/// <c>views.open</c> branches are swallowed and logged because the
/// decision has already been published -- raising them would turn a
/// missed cosmetic update into a duplicate orchestrator dispatch.
/// </para>
/// </remarks>
internal sealed class SlackInteractionHandler : ISlackInteractionHandler
{
    /// <summary>Slack interactive payload <c>type</c> for a Block Kit action.</summary>
    public const string BlockActionsType = "block_actions";

    /// <summary>Slack interactive payload <c>type</c> for a modal submission.</summary>
    public const string ViewSubmissionType = "view_submission";

    /// <summary>
    /// Messenger discriminator stamped on
    /// <see cref="HumanDecisionEvent.Messenger"/> for every decision
    /// the Slack connector publishes (architecture.md §2.9). Pinned as
    /// a constant so tests assert against the exact value.
    /// </summary>
    public const string MessengerName = "slack";

    /// <summary>
    /// Separator placed between the escalate modal's pinned verdict
    /// (<see cref="DefaultSlackMessageRenderer.EscalateActionValue"/>)
    /// and the operator's chosen severity tier when composing
    /// <see cref="HumanDecisionEvent.ActionValue"/>. Stage 6.1
    /// evaluator item 2: severity propagates through this namespacing
    /// (e.g., <c>"escalate:critical"</c>) because
    /// <see cref="HumanDecisionEvent"/> has no Metadata slot.
    /// Downstream consumers can route the verdict via
    /// <c>StartsWith("escalate")</c> and the urgency via
    /// <c>Split(':')[1]</c>.
    /// </summary>
    public const string EscalateSeveritySeparator = ":";

    /// <summary>
    /// Allow-list of Slack modal <c>callback_id</c> values that
    /// Stage 5.3 owns. View submissions with any other
    /// <c>callback_id</c> are ignored so an unrelated workspace modal
    /// (or a future modal type whose mapping has not yet been
    /// reviewed) cannot be silently converted into a
    /// <see cref="HumanDecisionEvent"/>.
    /// </summary>
    private static readonly HashSet<string> KnownViewCallbackIds = new(StringComparer.Ordinal)
    {
        DefaultSlackMessageRenderer.ReviewCallbackId,
        DefaultSlackMessageRenderer.EscalateCallbackId,
        SlackInteractionEncoding.CommentCallbackId,
    };

    /// <summary>
    /// Slack <c>views.open</c> error codes that classify the
    /// <c>trigger_id</c> as PERMANENTLY unusable (one-shot tokens with
    /// a ~3 second lifetime: once expired / exchanged / invalid, no
    /// retry will ever succeed). The async handler uses this set to
    /// distinguish a transient network/server hiccup from a structural
    /// "the fast-path didn't run inline" failure, so the dead-letter
    /// entry can be tagged accordingly and operators are not paged on
    /// every routine post-ACK arrival of a RequiresComment click.
    /// </summary>
    /// <remarks>
    /// Per the Slack <c>views.open</c> error reference: <c>expired_trigger_id</c>
    /// (token aged out of its ~3 s window), <c>trigger_exchanged</c> /
    /// <c>exchanged_trigger_id</c> (token already used by an earlier
    /// successful <c>views.open</c>), <c>invalid_trigger_id</c> /
    /// <c>trigger_expired</c> (Slack-side rejection that, like
    /// expiry, is non-retryable).
    /// </remarks>
    private static readonly HashSet<string> TerminalTriggerErrorCodes = new(StringComparer.Ordinal)
    {
        "expired_trigger_id",
        "trigger_expired",
        "trigger_exchanged",
        "exchanged_trigger_id",
        "invalid_trigger_id",
    };

    private readonly IAgentTaskService taskService;
    private readonly ISlackThreadMappingLookup threadMappingLookup;
    private readonly ISlackChatUpdateClient chatUpdateClient;
    private readonly ISlackViewsOpenClient viewsOpenClient;
    private readonly ISlackMessageRenderer messageRenderer;
    private readonly ILogger<SlackInteractionHandler> logger;
    private readonly TimeProvider timeProvider;

    public SlackInteractionHandler(
        IAgentTaskService taskService,
        ISlackThreadMappingLookup threadMappingLookup,
        ISlackChatUpdateClient chatUpdateClient,
        ISlackViewsOpenClient viewsOpenClient,
        ISlackMessageRenderer messageRenderer,
        ILogger<SlackInteractionHandler> logger,
        TimeProvider? timeProvider = null)
    {
        this.taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        this.threadMappingLookup = threadMappingLookup ?? throw new ArgumentNullException(nameof(threadMappingLookup));
        this.chatUpdateClient = chatUpdateClient ?? throw new ArgumentNullException(nameof(chatUpdateClient));
        this.viewsOpenClient = viewsOpenClient ?? throw new ArgumentNullException(nameof(viewsOpenClient));
        this.messageRenderer = messageRenderer ?? throw new ArgumentNullException(nameof(messageRenderer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        SlackInteractionDetail? detail = SlackInteractionPayloadDetailParser.TryParse(envelope.RawPayload);
        if (detail is null)
        {
            this.logger.LogWarning(
                "SlackInteractionHandler could not parse interactive payload for envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}; envelope discarded.",
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
            return;
        }

        switch (detail.Type)
        {
            case BlockActionsType:
                await this.HandleBlockActionsAsync(envelope, detail, ct).ConfigureAwait(false);
                return;

            case ViewSubmissionType:
                await this.HandleViewSubmissionAsync(envelope, detail, ct).ConfigureAwait(false);
                return;

            default:
                this.logger.LogInformation(
                    "SlackInteractionHandler ignoring unsupported interaction type='{Type}' envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}.",
                    detail.Type ?? "(none)",
                    envelope.IdempotencyKey,
                    envelope.TeamId,
                    envelope.UserId);
                return;
        }
    }

    private async Task HandleBlockActionsAsync(
        SlackInboundEnvelope envelope,
        SlackInteractionDetail detail,
        CancellationToken ct)
    {
        SlackInteractionAction? action = detail.PrimaryAction;
        if (action is null || string.IsNullOrEmpty(action.BlockId) || string.IsNullOrEmpty(action.Value))
        {
            this.logger.LogWarning(
                "SlackInteractionHandler block_actions payload missing action/block_id/value for envelope idempotency_key={IdempotencyKey}; envelope discarded.",
                envelope.IdempotencyKey);
            return;
        }

        if (!SlackInteractionEncoding.TryDecodeQuestionBlockId(
                action.BlockId,
                out string questionId,
                out bool requiresComment))
        {
            this.logger.LogWarning(
                "SlackInteractionHandler block_actions payload had unrecognised block_id='{BlockId}' for envelope idempotency_key={IdempotencyKey}; envelope discarded.",
                action.BlockId,
                envelope.IdempotencyKey);
            return;
        }

        string fallbackCorrelationId = ResolveFallbackCorrelationId(envelope);
        // SlackThreadMapping is keyed on the ROOT thread timestamp.
        // Prefer message.thread_ts (the parent thread's ts) and fall
        // back to message.ts ONLY when thread_ts is absent (which
        // Slack indicates the message IS the root).
        string? lookupKey = ResolveThreadLookupKey(detail);
        string correlationId = await this
            .ResolveCorrelationIdAsync(envelope, detail.ChannelId, lookupKey, fallbackCorrelationId, ct)
            .ConfigureAwait(false);

        if (requiresComment)
        {
            // Brief step 8: a button whose backing HumanAction.RequiresComment
            // is true MUST open a follow-up text-input modal instead of
            // publishing a decision directly. The comment modal carries
            // the pinned action value in its private_metadata so the
            // subsequent view_submission can reconstruct the
            // HumanDecisionEvent.
            await this
                .OpenCommentModalAsync(envelope, detail, action, questionId, correlationId, ct)
                .ConfigureAwait(false);
            return;
        }

        string externalUserId = detail.UserId ?? envelope.UserId ?? string.Empty;
        string externalMessageId = string.IsNullOrEmpty(detail.MessageTs)
            ? envelope.IdempotencyKey ?? string.Empty
            : detail.MessageTs!;

        HumanDecisionEvent decision = new(
            QuestionId: questionId,
            ActionValue: action.Value!,
            Comment: null,
            Messenger: MessengerName,
            ExternalUserId: externalUserId,
            ExternalMessageId: externalMessageId,
            ReceivedAt: this.timeProvider.GetUtcNow(),
            CorrelationId: correlationId);

        await this.taskService
            .PublishDecisionAsync(decision, ct)
            .ConfigureAwait(false);

        this.logger.LogInformation(
            "SlackInteractionHandler published HumanDecisionEvent question_id={QuestionId} action_value={ActionValue} correlation_id={CorrelationId} team={TeamId} channel={ChannelId} user={UserId}.",
            decision.QuestionId,
            decision.ActionValue,
            decision.CorrelationId,
            envelope.TeamId,
            detail.ChannelId,
            externalUserId);

        await this
            .DisableButtonsAsync(envelope, detail, action, decision, ct)
            .ConfigureAwait(false);
    }

    private async Task HandleViewSubmissionAsync(
        SlackInboundEnvelope envelope,
        SlackInteractionDetail detail,
        CancellationToken ct)
    {
        if (detail.View is null)
        {
            this.logger.LogWarning(
                "SlackInteractionHandler view_submission payload missing view for envelope idempotency_key={IdempotencyKey}; envelope discarded.",
                envelope.IdempotencyKey);
            return;
        }

        // Gate on callback_id. Slack delivers ANY workspace modal
        // submission to this endpoint -- including modals opened by
        // unrelated apps that happen to share the bot's signing secret
        // if a workspace re-uses it. Stage 5.3 only owns the review /
        // escalate / comment modals; unknown callback_ids MUST be
        // discarded so a third-party modal whose private_metadata
        // happens to look agent-shaped cannot be converted into a
        // HumanDecisionEvent.
        if (!IsRecognizedViewCallback(detail.View.CallbackId))
        {
            this.logger.LogInformation(
                "SlackInteractionHandler ignoring view_submission with unrecognised callback_id='{CallbackId}' envelope idempotency_key={IdempotencyKey} view_id={ViewId}.",
                detail.View.CallbackId ?? "(none)",
                envelope.IdempotencyKey,
                detail.View.Id);
            return;
        }

        SlackPrivateMetadata metadata = SlackPrivateMetadata.Parse(detail.View.PrivateMetadata);

        // The architecture mapping table keys HumanDecisionEvent on
        // QuestionId; the renderer MUST encode questionId in
        // private_metadata. TaskId-as-fallback was intentionally NOT
        // accepted because it masks renderer bugs and would let
        // arbitrary task-shaped metadata leak into the decision
        // pipeline. The Stage 5.1 renderer encodes
        // questionId = TaskId for /agent review and /agent escalate.
        string? questionId = metadata.QuestionId;
        if (string.IsNullOrEmpty(questionId))
        {
            this.logger.LogWarning(
                "SlackInteractionHandler view_submission private_metadata had no questionId for envelope idempotency_key={IdempotencyKey} view_id={ViewId} callback_id={CallbackId}; envelope discarded.",
                envelope.IdempotencyKey,
                detail.View.Id,
                detail.View.CallbackId);
            return;
        }

        // Pinned ActionValue (from a comment modal opened by a
        // RequiresComment button click, or from the escalate modal
        // whose verdict is the literal "escalate") wins over any
        // in-view select; when the modal was opened from /agent review
        // the verdict comes from the static_select in
        // view.state.values. The FirstStaticSelectValueFallback (raw
        // "value" property on any action, including plain_text_input)
        // is intentionally NOT used here -- letting a text input's
        // value leak into ActionValue produces malformed escalation
        // decisions. Modals MUST either supply a static_select
        // (review) or pin actionValue in private_metadata (comment,
        // escalate); anything else is discarded by the
        // missing-verdict guard below.
        string? actionValue = metadata.ActionValue
            ?? detail.View.FirstStaticSelectValue;
        if (string.IsNullOrEmpty(actionValue))
        {
            this.logger.LogWarning(
                "SlackInteractionHandler view_submission had no verdict (no private_metadata.actionValue and no static_select selection) for envelope idempotency_key={IdempotencyKey} view_id={ViewId} question_id={QuestionId}; envelope discarded.",
                envelope.IdempotencyKey,
                detail.View.Id,
                questionId);
            return;
        }

        // Stage 6.1 evaluator item 2: propagate the escalate modal's
        // severity static_select into the typed HumanDecisionEvent by
        // namespacing the verdict as "escalate:<severity>" (e.g.,
        // "escalate:critical"). HumanDecisionEvent has no Metadata
        // slot (architecture.md §3.6.3 pins eight fields), so we
        // encode the urgency tier into ActionValue itself; downstream
        // consumers can route on the bare verdict via
        // StartsWith("escalate") and on the urgency via the suffix.
        // The composition only fires for the escalate callback so
        // /agent review's verdict ("approve" / "request-changes" /
        // "reject") is never wrapped. The pinned base value
        // ("escalate", from private_metadata.actionValue) is the
        // source of truth for the verdict half; FirstStaticSelectValue
        // is the user's severity choice. When the user submits without
        // touching the select Slack still echoes the initial_option
        // (Warning), so production submissions always carry severity.
        //
        // Idempotence guard: skip composition when the pinned base is
        // ALREADY a namespaced "escalate:<...>" value (a hand-rolled
        // modal that pinned the severity directly), otherwise the
        // suffix would be appended a second time (producing
        // "escalate:warning:warning"). The guard asserts the SPECIFIC
        // "escalate:" prefix rather than the bare separator -- a
        // generic Contains(':') would silently suppress composition
        // for any future pinned value that happens to carry a colon
        // for an unrelated reason.
        if (string.Equals(detail.View.CallbackId, DefaultSlackMessageRenderer.EscalateCallbackId, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(detail.View.FirstStaticSelectValue)
            && !actionValue!.StartsWith(DefaultSlackMessageRenderer.EscalateActionValue + EscalateSeveritySeparator, StringComparison.Ordinal))
        {
            actionValue = string.Concat(actionValue, EscalateSeveritySeparator, detail.View.FirstStaticSelectValue);
        }

        string? comment = detail.View.FirstPlainTextInputValue;

        // Correlation-id precedence: private_metadata.correlationId
        // (pinned when the modal was opened from a button click or
        // from /agent review so the originating thread / slash command
        // shares the audit row) wins; otherwise fall back to a thread
        // lookup. Prefer the pinned threadTs over messageTs because
        // SlackThreadMapping is keyed on the thread ROOT timestamp.
        string correlationId = !string.IsNullOrEmpty(metadata.CorrelationId)
            ? metadata.CorrelationId!
            : await this
                .ResolveCorrelationIdAsync(
                    envelope,
                    metadata.ChannelId,
                    !string.IsNullOrEmpty(metadata.ThreadTs) ? metadata.ThreadTs : metadata.MessageTs,
                    ResolveFallbackCorrelationId(envelope),
                    ct)
                .ConfigureAwait(false);

        string externalUserId = detail.UserId
            ?? metadata.UserId
            ?? envelope.UserId
            ?? string.Empty;

        string externalMessageId = string.IsNullOrEmpty(detail.View.Id)
            ? envelope.IdempotencyKey ?? string.Empty
            : detail.View.Id!;

        HumanDecisionEvent decision = new(
            QuestionId: questionId!,
            ActionValue: actionValue!,
            Comment: comment,
            Messenger: MessengerName,
            ExternalUserId: externalUserId,
            ExternalMessageId: externalMessageId,
            ReceivedAt: this.timeProvider.GetUtcNow(),
            CorrelationId: correlationId);

        await this.taskService
            .PublishDecisionAsync(decision, ct)
            .ConfigureAwait(false);

        this.logger.LogInformation(
            "SlackInteractionHandler published view_submission HumanDecisionEvent question_id={QuestionId} action_value={ActionValue} has_comment={HasComment} correlation_id={CorrelationId} team={TeamId} user={UserId} view_id={ViewId}.",
            decision.QuestionId,
            decision.ActionValue,
            !string.IsNullOrEmpty(decision.Comment),
            decision.CorrelationId,
            envelope.TeamId,
            externalUserId,
            detail.View.Id);

        // When the modal was opened from a RequiresComment button
        // click, the metadata carries the parent message coordinates;
        // disable the originating buttons now that the decision has
        // landed. For /agent review / /agent escalate flows the
        // metadata has no messageTs and DisableButtonsAsync degrades
        // to a logged "skipped" outcome.
        if (!string.IsNullOrEmpty(metadata.MessageTs))
        {
            SlackInteractionAction pseudoAction = new(
                BlockId: null,
                ActionId: null,
                Value: actionValue,
                Label: metadata.ActionLabel ?? actionValue);
            SlackInteractionDetail viewParent = detail with
            {
                ChannelId = metadata.ChannelId,
                MessageTs = metadata.MessageTs,
            };
            await this
                .DisableButtonsAsync(envelope, viewParent, pseudoAction, decision, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task OpenCommentModalAsync(
        SlackInboundEnvelope envelope,
        SlackInteractionDetail detail,
        SlackInteractionAction action,
        string questionId,
        string correlationId,
        CancellationToken ct)
    {
        // Every failure mode in this method throws (rather than
        // log-and-return). Reason: when a RequiresComment button click
        // fails to open its follow-up modal, NO HumanDecisionEvent is
        // published, the originating buttons remain live, AND the
        // inbound envelope has already been ACKed to Slack -- so
        // silent-return would lose the human action with no retry /
        // dead-letter signal. Throwing lets the
        // SlackInboundProcessingPipeline route the envelope through
        // its retry / dead-letter machinery. Two distinct exception
        // shapes are used so operators can triage the DLQ entry
        // without reading attempt-by-attempt logs:
        //   * SlackTriggerExpiredException -- the views.open call
        //     returned one of the Slack-side terminal trigger codes
        //     (expired_trigger_id, trigger_exchanged, ...). These are
        //     PERMANENT failures: trigger_ids are one-shot tokens with
        //     a ~3 s lifetime, so every retry attempt will also fail.
        //     This almost always means the synchronous fast-path
        //     (DefaultSlackInteractionFastPathHandler) is not wired
        //     (or NoOpSlackModalFastPathHandler is registered), so the
        //     RequiresComment click is reaching the async handler
        //     post-ACK after the trigger_id has already aged out. The
        //     exception message is prefixed "permanently failed --
        //     trigger_id expired" so the resulting
        //     SlackDeadLetterEntry.Reason is greppable by alerting,
        //     and the failure is logged at Error level so operators
        //     are paged BEFORE the retry budget exhausts.
        //   * InvalidOperationException -- transient transport /
        //     renderer / missing-trigger failures. Retries may succeed
        //     (e.g., the next Slack delivery carries a fresh
        //     trigger_id, or a network blip recovers). Logged at
        //     Warning.
        string? triggerId = detail.TriggerId ?? envelope.TriggerId;
        if (string.IsNullOrEmpty(triggerId))
        {
            throw new InvalidOperationException(
                $"SlackInteractionHandler cannot open comment modal: trigger_id missing on envelope idempotency_key={envelope.IdempotencyKey} question_id={questionId}.");
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
            this.logger.LogError(
                ex,
                "SlackInteractionHandler comment-modal renderer failed for envelope idempotency_key={IdempotencyKey} question_id={QuestionId}; propagating so the pipeline can retry / dead-letter.",
                envelope.IdempotencyKey,
                questionId);
            throw;
        }

        SlackViewsOpenResult result = await this.viewsOpenClient
            .OpenAsync(new SlackViewsOpenRequest(envelope.TeamId, triggerId, viewPayload), ct)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            this.logger.LogInformation(
                "SlackInteractionHandler opened comment modal for question_id={QuestionId} team={TeamId} user={UserId} trigger_id={TriggerId}.",
                questionId,
                envelope.TeamId,
                detail.UserId,
                triggerId);
            return;
        }

        // Distinguish permanent Slack-side trigger failures from
        // transient transport hiccups. Permanent failures get a
        // dedicated exception type AND an Error-level log so the
        // dead-letter entry's Reason carries the "permanently failed
        // -- trigger_id expired" tag and operators are paged on the
        // FIRST attempt rather than after the retry budget exhausts.
        if (result.Kind == SlackViewsOpenResultKind.SlackError
            && IsTerminalTriggerError(result.Error))
        {
            this.logger.LogError(
                "SlackInteractionHandler comment-modal views.open returned terminal Slack error '{SlackError}' for question_id={QuestionId} team={TeamId} trigger_id={TriggerId} envelope idempotency_key={IdempotencyKey}: trigger_id is one-shot and ~3 s lived, so every retry will also fail permanently. This almost always means the synchronous fast-path (DefaultSlackInteractionFastPathHandler) is not wired (or NoOpSlackModalFastPathHandler is registered) -- RequiresComment clicks MUST be handled inline before the HTTP ACK so views.open executes within Slack's trigger_id lifetime. Dead-lettering as permanently failed.",
                result.Error,
                questionId,
                envelope.TeamId,
                triggerId,
                envelope.IdempotencyKey);
            throw new SlackTriggerExpiredException(
                envelope.IdempotencyKey ?? string.Empty,
                questionId,
                envelope.TeamId,
                triggerId,
                result.Error ?? "(none)");
        }

        // Transient / non-trigger Slack error -- log Warning and throw
        // so the pipeline retries / dead-letters under normal budget.
        this.logger.LogWarning(
            "SlackInteractionHandler comment-modal views.open failed kind={Kind} error={Error} question_id={QuestionId} team={TeamId} trigger_id={TriggerId}; propagating so the pipeline can retry / dead-letter.",
            result.Kind,
            result.Error,
            questionId,
            envelope.TeamId,
            triggerId);
        throw new InvalidOperationException(
            $"Slack views.open failed for comment modal question_id={questionId} kind={result.Kind} error={result.Error ?? "(none)"}.");
    }

    private async Task DisableButtonsAsync(
        SlackInboundEnvelope envelope,
        SlackInteractionDetail detail,
        SlackInteractionAction action,
        HumanDecisionEvent decision,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(detail.ChannelId) || string.IsNullOrEmpty(detail.MessageTs))
        {
            this.logger.LogDebug(
                "SlackInteractionHandler skipping chat.update for question_id={QuestionId}: no channel_id / message.ts on payload.",
                decision.QuestionId);
            return;
        }

        string verb = string.IsNullOrEmpty(action.Label) ? action.Value ?? decision.ActionValue : action.Label!;
        string headline = $"*{verb}* by <@{decision.ExternalUserId}>";
        string fallback = $"{verb} by {decision.ExternalUserId}";
        object blocks = new object[]
        {
            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = headline,
                },
            },
            new
            {
                type = "context",
                elements = new object[]
                {
                    new
                    {
                        type = "mrkdwn",
                        text = $"Decision recorded for question `{decision.QuestionId}` at {decision.ReceivedAt:O}.",
                    },
                },
            },
        };

        SlackChatUpdateRequest updateRequest = new(
            TeamId: envelope.TeamId,
            ChannelId: detail.ChannelId!,
            MessageTs: detail.MessageTs!,
            Text: fallback,
            Blocks: blocks);

        SlackChatUpdateResult result;
        try
        {
            result = await this.chatUpdateClient
                .UpdateAsync(updateRequest, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // chat.update is best-effort: the decision has already
            // been published. Log and swallow.
            this.logger.LogWarning(
                ex,
                "SlackInteractionHandler chat.update threw for question_id={QuestionId} team={TeamId} channel={ChannelId} ts={MessageTs}.",
                decision.QuestionId,
                envelope.TeamId,
                detail.ChannelId,
                detail.MessageTs);
            return;
        }

        if (!result.IsSuccess)
        {
            this.logger.LogWarning(
                "SlackInteractionHandler chat.update non-success kind={Kind} error={Error} for question_id={QuestionId} team={TeamId} channel={ChannelId} ts={MessageTs}.",
                result.Kind,
                result.Error,
                decision.QuestionId,
                envelope.TeamId,
                detail.ChannelId,
                detail.MessageTs);
        }
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

        // DB / lookup failures MUST propagate so the inbound pipeline
        // can retry / dead-letter. Silently degrading to the envelope
        // idempotency key would publish the decision with a wrong
        // CorrelationId, breaking the story-level "every agent/human
        // exchange is queryable by correlation id" acceptance
        // criterion.
        SlackThreadMapping? mapping = await this.threadMappingLookup
            .LookupAsync(envelope.TeamId, channelId, threadTs, ct)
            .ConfigureAwait(false);

        if (mapping is null || string.IsNullOrEmpty(mapping.CorrelationId))
        {
            // A legitimate "no row" outcome -- the click landed on a
            // message that was not anchored to an agent task. Falling
            // back to the envelope idempotency key keeps the decision
            // queryable by a deterministic anchor.
            return fallback;
        }

        return mapping.CorrelationId;
    }

    /// <summary>
    /// Resolves the timestamp the <see cref="SlackThreadMapping"/>
    /// lookup should key on. Prefers
    /// <see cref="SlackInteractionDetail.ThreadTs"/> (the parent
    /// thread's root ts -- the column the mapping table is indexed on)
    /// and falls back to <see cref="SlackInteractionDetail.MessageTs"/>
    /// ONLY when <c>thread_ts</c> is absent (which Slack signals by
    /// omission for clicks on a thread's root message).
    /// </summary>
    private static string? ResolveThreadLookupKey(SlackInteractionDetail detail)
        => !string.IsNullOrEmpty(detail.ThreadTs) ? detail.ThreadTs : detail.MessageTs;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="callbackId"/>
    /// matches one of the Stage 5.3-owned modal callbacks
    /// (<see cref="KnownViewCallbackIds"/>).
    /// </summary>
    private static bool IsRecognizedViewCallback(string? callbackId)
        => !string.IsNullOrEmpty(callbackId) && KnownViewCallbackIds.Contains(callbackId);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="slackError"/>
    /// is one of the Slack <c>views.open</c> error codes that classify
    /// the <c>trigger_id</c> as permanently unusable (one-shot tokens
    /// with a ~3 second lifetime -- no retry will ever succeed). See
    /// <see cref="TerminalTriggerErrorCodes"/> for the canonical list.
    /// </summary>
    private static bool IsTerminalTriggerError(string? slackError)
        => !string.IsNullOrEmpty(slackError) && TerminalTriggerErrorCodes.Contains(slackError);

    private static string ResolveFallbackCorrelationId(SlackInboundEnvelope envelope)
        => string.IsNullOrEmpty(envelope.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : envelope.IdempotencyKey;
}

/// <summary>
/// Thrown by <see cref="SlackInteractionHandler"/> when a
/// <c>RequiresComment</c> button click reaches the async handler and
/// the resulting <c>views.open</c> call returns a Slack-side terminal
/// trigger error (<c>expired_trigger_id</c>, <c>trigger_exchanged</c>,
/// <c>invalid_trigger_id</c>, ...). The trigger_id is a one-shot token
/// with a ~3 second lifetime, so this failure is PERMANENT -- every
/// retry attempt will also fail. The exception's
/// <see cref="Exception.Message"/> is prefixed
/// <c>"permanently failed -- trigger_id expired"</c> so the resulting
/// <see cref="Queues.SlackDeadLetterEntry.Reason"/> is greppable for
/// operator alerting / triage.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so existing
/// tests / callers that assert against <see cref="InvalidOperationException"/>
/// (the previously shipped error type for views.open failures) keep
/// passing while still allowing precise <c>catch</c> /
/// <c>is SlackTriggerExpiredException</c> handling at sites that want
/// to special-case the permanent failure (e.g., a future retry policy
/// that classifies terminal exceptions as non-retryable).
/// </para>
/// <para>
/// This condition almost always indicates that the synchronous
/// <see cref="DefaultSlackInteractionFastPathHandler"/> is not wired
/// (or <see cref="Transport.NoOpSlackModalFastPathHandler"/> is the
/// active registration) -- RequiresComment clicks are supposed to be
/// handled inline BEFORE the HTTP ACK so <c>views.open</c> executes
/// within Slack's trigger_id lifetime. The
/// <see cref="DiagnosticHint"/> property surfaces that hint to log
/// formatters and dashboards without requiring them to re-parse
/// <see cref="Exception.Message"/>.
/// </para>
/// </remarks>
internal sealed class SlackTriggerExpiredException : InvalidOperationException
{
    /// <summary>
    /// Diagnostic prefix pinned on every instance's
    /// <see cref="Exception.Message"/> so the resulting dead-letter
    /// entry's <see cref="Queues.SlackDeadLetterEntry.Reason"/> can be
    /// matched with a simple <c>startswith</c> / <c>contains</c> query
    /// in operator dashboards and alerting rules. Stable string --
    /// existing alerts SHOULD match on this exact prefix.
    /// </summary>
    public const string ReasonPrefix = "permanently failed -- trigger_id expired";

    /// <summary>
    /// Human-readable hint surfacing the most likely root cause
    /// (synchronous fast-path not wired). Kept short so it fits in
    /// alert summaries without truncation.
    /// </summary>
    public const string DiagnosticHint =
        "trigger_id is one-shot and ~3 s lived; this almost always means the synchronous fast-path is not wired (DefaultSlackInteractionFastPathHandler missing, or NoOpSlackModalFastPathHandler registered) -- RequiresComment clicks must run inline before the HTTP ACK so views.open executes within Slack's trigger_id lifetime.";

    public SlackTriggerExpiredException(
        string idempotencyKey,
        string questionId,
        string teamId,
        string triggerId,
        string slackError)
        : base(BuildMessage(idempotencyKey, questionId, teamId, triggerId, slackError))
    {
        this.IdempotencyKey = idempotencyKey ?? string.Empty;
        this.QuestionId = questionId ?? string.Empty;
        this.TeamId = teamId ?? string.Empty;
        this.TriggerId = triggerId ?? string.Empty;
        this.SlackError = slackError ?? string.Empty;
    }

    /// <summary>Inbound envelope idempotency key (for log correlation).</summary>
    public string IdempotencyKey { get; }

    /// <summary>Decoded QuestionId of the click that could not open its modal.</summary>
    public string QuestionId { get; }

    /// <summary>Slack workspace identifier of the originating click.</summary>
    public string TeamId { get; }

    /// <summary>The expired trigger_id (logged so operators can confirm the token, not for replay).</summary>
    public string TriggerId { get; }

    /// <summary>Raw Slack error string returned by <c>views.open</c>.</summary>
    public string SlackError { get; }

    private static string BuildMessage(
        string idempotencyKey,
        string questionId,
        string teamId,
        string triggerId,
        string slackError)
    {
        return $"{ReasonPrefix}: slack_error='{slackError}' question_id='{questionId}' team_id='{teamId}' trigger_id='{triggerId}' idempotency_key='{idempotencyKey}'. {DiagnosticHint}";
    }
}

/// <summary>
/// Canonical view of an interactive payload extracted from
/// <see cref="SlackInboundEnvelope.RawPayload"/>. Carries the fields
/// the Stage 5.3 handler actually consumes; trimmed compared to the
/// underlying Slack JSON so the handler does not re-parse the document
/// for every field.
/// </summary>
internal sealed record SlackInteractionDetail
{
    public string? Type { get; init; }

    public string? TeamId { get; init; }

    public string? ChannelId { get; init; }

    public string? UserId { get; init; }

    public string? TriggerId { get; init; }

    /// <summary>Slack <c>message.ts</c> of the parent message (button clicks only).</summary>
    public string? MessageTs { get; init; }

    /// <summary>
    /// Slack <c>message.thread_ts</c> of the parent message. Present
    /// only when the clicked message lives inside an existing thread;
    /// absent when the click landed on the thread's root message
    /// (Slack does NOT emit <c>thread_ts</c> in that case because the
    /// root's <see cref="MessageTs"/> already IS the thread ts). The
    /// <see cref="Entities.SlackThreadMapping"/> table is keyed on the
    /// thread ROOT timestamp, so the correlation lookup MUST prefer
    /// this value over <see cref="MessageTs"/> whenever it is non-null
    /// -- otherwise in-thread reply clicks would miss the mapping.
    /// </summary>
    public string? ThreadTs { get; init; }

    /// <summary>First <c>actions[]</c> entry (button clicks only).</summary>
    public SlackInteractionAction? PrimaryAction { get; init; }

    /// <summary>The <c>view</c> sub-object (modal submissions only).</summary>
    public SlackInteractionView? View { get; init; }
}

/// <summary>
/// Canonical view of a single Block Kit action inside an interactive
/// payload (the Stage 5.3 handler only consumes the first action even
/// when Slack delivers several).
/// </summary>
internal sealed record SlackInteractionAction(
    string? BlockId,
    string? ActionId,
    string? Value,
    string? Label);

/// <summary>
/// Canonical view of a Slack modal payload nested under <c>view</c>.
/// </summary>
internal sealed record SlackInteractionView
{
    public string? Id { get; init; }

    public string? CallbackId { get; init; }

    public string? PrivateMetadata { get; init; }

    /// <summary>First <c>selected_option.value</c> across every
    /// <c>static_select</c> action in <c>view.state.values</c>.</summary>
    public string? FirstStaticSelectValue { get; init; }

    /// <summary>Fallback that walks every action's <c>value</c> field
    /// when no <c>selected_option</c> exists (handles
    /// <c>radio_buttons</c> / <c>checkboxes</c>).</summary>
    public string? FirstStaticSelectValueFallback { get; init; }

    /// <summary>First non-empty <c>plain_text_input</c>
    /// <c>value</c>.</summary>
    public string? FirstPlainTextInputValue { get; init; }
}

/// <summary>
/// Parsed shape of a modal's <c>private_metadata</c> JSON. Tolerant of
/// missing fields so the handler can degrade rather than throw.
/// </summary>
internal sealed record SlackPrivateMetadata
{
    public string? QuestionId { get; init; }

    /// <summary>
    /// Optional legacy <c>taskId</c> key. Parsed for tolerance with
    /// older renderers / audit logs that still emitted it, but the
    /// Stage 5.3 handler does NOT use it as a <c>QuestionId</c>
    /// fallback -- the architecture mapping table requires an
    /// explicit <c>questionId</c>.
    /// </summary>
    public string? TaskId { get; init; }

    public string? ActionValue { get; init; }

    public string? ActionLabel { get; init; }

    public string? ChannelId { get; init; }

    public string? MessageTs { get; init; }

    /// <summary>
    /// Parent thread root timestamp pinned at modal-open time so the
    /// view_submission can resolve the same <c>SlackThreadMapping</c>
    /// row the originating button click did.
    /// </summary>
    public string? ThreadTs { get; init; }

    public string? UserId { get; init; }

    public string? CorrelationId { get; init; }

    public static SlackPrivateMetadata Parse(string? privateMetadata)
    {
        if (string.IsNullOrEmpty(privateMetadata))
        {
            return new SlackPrivateMetadata();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(privateMetadata);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // Some hosts encode private_metadata as a raw string
                // (e.g., the bare question id). Treat the whole value
                // as the question id when JSON parsing succeeds but
                // yields a non-object.
                return root.ValueKind == JsonValueKind.String
                    ? new SlackPrivateMetadata { QuestionId = root.GetString() }
                    : new SlackPrivateMetadata();
            }

            return new SlackPrivateMetadata
            {
                QuestionId = ReadStringProperty(root, "questionId"),
                TaskId = ReadStringProperty(root, "taskId"),
                ActionValue = ReadStringProperty(root, "actionValue"),
                ActionLabel = ReadStringProperty(root, "actionLabel"),
                ChannelId = ReadStringProperty(root, "channelId"),
                MessageTs = ReadStringProperty(root, "messageTs"),
                ThreadTs = ReadStringProperty(root, "threadTs"),
                UserId = ReadStringProperty(root, "userId"),
                CorrelationId = ReadStringProperty(root, "correlationId"),
            };
        }
        catch (JsonException)
        {
            // Some hosts (incl. the brief's Stage 4.1 stub) put a bare
            // string in private_metadata. Treat the whole value as the
            // question id so the handler can still publish.
            return new SlackPrivateMetadata { QuestionId = privateMetadata };
        }
    }

    private static string? ReadStringProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>
/// Parser that decodes Slack interactive payloads into the
/// <see cref="SlackInteractionDetail"/> shape consumed by
/// <see cref="SlackInteractionHandler"/>. Handles both the HTTP form
/// envelope (<c>payload=&lt;url-encoded JSON&gt;</c>) AND the Socket
/// Mode JSON normalisation (raw JSON object in
/// <see cref="SlackInboundEnvelope.RawPayload"/>).
/// </summary>
internal static class SlackInteractionPayloadDetailParser
{
    public static SlackInteractionDetail? TryParse(string? rawPayload)
    {
        if (string.IsNullOrEmpty(rawPayload))
        {
            return null;
        }

        string json = ExtractInteractionJson(rawPayload);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? type = ReadStringProperty(root, "type");
            string? triggerId = ReadStringProperty(root, "trigger_id");

            string? teamId = null;
            if (root.TryGetProperty("team", out JsonElement teamObj) && teamObj.ValueKind == JsonValueKind.Object)
            {
                teamId = ReadStringProperty(teamObj, "id");
            }

            string? channelId = null;
            if (root.TryGetProperty("channel", out JsonElement channelObj) && channelObj.ValueKind == JsonValueKind.Object)
            {
                channelId = ReadStringProperty(channelObj, "id");
            }

            string? userId = null;
            if (root.TryGetProperty("user", out JsonElement userObj) && userObj.ValueKind == JsonValueKind.Object)
            {
                userId = ReadStringProperty(userObj, "id");
            }

            string? messageTs = null;
            string? threadTs = null;
            if (root.TryGetProperty("message", out JsonElement messageObj) && messageObj.ValueKind == JsonValueKind.Object)
            {
                messageTs = ReadStringProperty(messageObj, "ts");

                // Slack ships message.thread_ts ONLY when the clicked
                // message lives inside an existing thread. When the
                // click landed on the root message itself, thread_ts is
                // absent and message.ts IS the root timestamp.
                // SlackThreadMapping is keyed on the ROOT timestamp,
                // so the lookup MUST prefer thread_ts whenever it
                // exists -- otherwise every in-thread reply would miss
                // the mapping.
                threadTs = ReadStringProperty(messageObj, "thread_ts");
            }

            // Slack also delivers the parent thread ts on
            // container.thread_ts / container.message_ts for some
            // surfaces (e.g., message shortcuts). Honour them when the
            // message object did not supply equivalents.
            if (root.TryGetProperty("container", out JsonElement containerObj)
                && containerObj.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrEmpty(messageTs))
                {
                    messageTs = ReadStringProperty(containerObj, "message_ts");
                }

                if (string.IsNullOrEmpty(threadTs))
                {
                    threadTs = ReadStringProperty(containerObj, "thread_ts");
                }
            }

            SlackInteractionAction? primaryAction = null;
            if (root.TryGetProperty("actions", out JsonElement actions)
                && actions.ValueKind == JsonValueKind.Array
                && actions.GetArrayLength() > 0)
            {
                JsonElement first = actions[0];
                if (first.ValueKind == JsonValueKind.Object)
                {
                    primaryAction = ParseAction(first);
                }
            }

            SlackInteractionView? view = null;
            if (root.TryGetProperty("view", out JsonElement viewObj) && viewObj.ValueKind == JsonValueKind.Object)
            {
                view = ParseView(viewObj);
            }

            return new SlackInteractionDetail
            {
                Type = type,
                TeamId = teamId,
                ChannelId = channelId,
                UserId = userId,
                TriggerId = triggerId,
                MessageTs = messageTs,
                ThreadTs = threadTs,
                PrimaryAction = primaryAction,
                View = view,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SlackInteractionAction ParseAction(JsonElement element)
    {
        string? blockId = ReadStringProperty(element, "block_id");
        string? actionId = ReadStringProperty(element, "action_id");
        string? value = ReadStringProperty(element, "value");
        string? label = null;
        if (element.TryGetProperty("text", out JsonElement textObj) && textObj.ValueKind == JsonValueKind.Object)
        {
            label = ReadStringProperty(textObj, "text");
        }

        // Some action types (static_select, radio_buttons) carry the
        // chosen value under selected_option.value instead of value.
        if (string.IsNullOrEmpty(value)
            && element.TryGetProperty("selected_option", out JsonElement selectedOption)
            && selectedOption.ValueKind == JsonValueKind.Object)
        {
            value = ReadStringProperty(selectedOption, "value");
        }

        return new SlackInteractionAction(blockId, actionId, value, label);
    }

    private static SlackInteractionView ParseView(JsonElement viewObj)
    {
        string? id = ReadStringProperty(viewObj, "id");
        string? callbackId = ReadStringProperty(viewObj, "callback_id");
        string? privateMetadata = ReadStringProperty(viewObj, "private_metadata");

        string? selectValue = null;
        string? fallbackValue = null;
        string? textValue = null;

        if (viewObj.TryGetProperty("state", out JsonElement stateObj)
            && stateObj.ValueKind == JsonValueKind.Object
            && stateObj.TryGetProperty("values", out JsonElement valuesObj)
            && valuesObj.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty block in valuesObj.EnumerateObject())
            {
                if (block.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty actionEntry in block.Value.EnumerateObject())
                {
                    if (actionEntry.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? actionType = ReadStringProperty(actionEntry.Value, "type");
                    if (string.Equals(actionType, "static_select", StringComparison.Ordinal)
                        || string.Equals(actionType, "external_select", StringComparison.Ordinal)
                        || string.Equals(actionType, "radio_buttons", StringComparison.Ordinal))
                    {
                        if (string.IsNullOrEmpty(selectValue)
                            && actionEntry.Value.TryGetProperty("selected_option", out JsonElement selectedOption)
                            && selectedOption.ValueKind == JsonValueKind.Object)
                        {
                            selectValue = ReadStringProperty(selectedOption, "value");
                        }
                    }

                    if (string.Equals(actionType, "plain_text_input", StringComparison.Ordinal))
                    {
                        if (string.IsNullOrEmpty(textValue))
                        {
                            textValue = ReadStringProperty(actionEntry.Value, "value");
                        }
                    }

                    // Generic fallback so the handler can degrade to
                    // whatever value the action carries when the type
                    // is not recognised.
                    if (string.IsNullOrEmpty(fallbackValue))
                    {
                        string? raw = ReadStringProperty(actionEntry.Value, "value");
                        if (!string.IsNullOrEmpty(raw))
                        {
                            fallbackValue = raw;
                        }
                    }
                }
            }
        }

        return new SlackInteractionView
        {
            Id = id,
            CallbackId = callbackId,
            PrivateMetadata = privateMetadata,
            FirstStaticSelectValue = selectValue,
            FirstStaticSelectValueFallback = fallbackValue,
            FirstPlainTextInputValue = textValue,
        };
    }

    private static string ExtractInteractionJson(string rawPayload)
    {
        // Look ahead past whitespace to decide whether this is a raw
        // JSON object (Socket Mode normaliser delivery) or an
        // application/x-www-form-urlencoded form body wrapping the
        // payload inside payload=&lt;url-encoded JSON&gt; (HTTP
        // controller delivery).
        for (int i = 0; i < rawPayload.Length; i++)
        {
            char c = rawPayload[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                continue;
            }

            if (c == '{')
            {
                return rawPayload;
            }

            break;
        }

        IDictionary<string, Microsoft.Extensions.Primitives.StringValues> fields =
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(rawPayload);
        if (!fields.TryGetValue("payload", out Microsoft.Extensions.Primitives.StringValues values)
            || Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(values))
        {
            return string.Empty;
        }

        return values.ToString();
    }

    private static string? ReadStringProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
