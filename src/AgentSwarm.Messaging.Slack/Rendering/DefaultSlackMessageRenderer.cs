// -----------------------------------------------------------------------
// <copyright file="DefaultSlackMessageRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Rendering;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Stage 5.1 default <see cref="ISlackMessageRenderer"/>. Produces a
/// task-id-aware Block Kit modal payload for <c>/agent review</c> and
/// <c>/agent escalate</c>; serialises a structured
/// <c>private_metadata</c> blob (JSON) so the Stage 5.3 view-submission
/// handler can recover both the task-id and the correlation-id of the
/// originating command.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.1's <c>DefaultSlackModalPayloadBuilder</c> only stored
/// <see cref="Transport.SlackInboundEnvelope.IdempotencyKey"/> in
/// <c>private_metadata</c>; the task-id supplied to the command never
/// reached the modal. Stage 5.1's renderer puts the task-id in both the
/// machine-readable <c>private_metadata</c> JSON envelope AND the
/// human-readable title / context block so reviewers see what they are
/// being asked to act on.
/// </para>
/// </remarks>
internal sealed class DefaultSlackMessageRenderer : ISlackMessageRenderer
{
    /// <summary>
    /// <c>callback_id</c> used by the review modal. Stage 5.3's
    /// view-submission handler keys on this constant.
    /// </summary>
    public const string ReviewCallbackId = "agent_review_modal";

    /// <summary>
    /// <c>callback_id</c> used by the escalate modal.
    /// </summary>
    public const string EscalateCallbackId = "agent_escalate_modal";

    /// <inheritdoc />
    public object RenderReviewModal(SlackReviewModalContext context)
    {
        // The Stage 5.3 view-submission handler requires a `questionId`
        // key inside private_metadata (the architecture's mapping table
        // keys HumanDecisionEvent on QuestionId). For the task-level
        // /agent review flow the task id IS the question id (a single
        // human is being asked "what do you decide about this task?"),
        // so we encode questionId = TaskId. The legacy taskId /
        // subCommand keys remain for backward-compatible audit
        // consumers.
        string privateMetadata = SerializePrivateMetadata(
            questionId: context.TaskId,
            taskId: context.TaskId,
            subCommand: "review",
            correlationId: context.CorrelationId);

        return new
        {
            type = "modal",
            callback_id = ReviewCallbackId,
            private_metadata = privateMetadata,
            title = new { type = "plain_text", text = BuildModalTitle("Review", context.TaskId) },
            submit = new { type = "plain_text", text = "Submit" },
            close = new { type = "plain_text", text = "Cancel" },
            blocks = new object[]
            {
                new
                {
                    type = "section",
                    block_id = "review_context",
                    text = new
                    {
                        type = "mrkdwn",
                        text = $"*Review request* for task `{context.TaskId}` from <@{context.UserId}> in <#{context.ChannelId}>",
                    },
                },
                new
                {
                    type = "input",
                    block_id = "review_decision",
                    label = new { type = "plain_text", text = "Decision" },
                    element = new
                    {
                        type = "static_select",
                        action_id = "review_decision_select",
                        placeholder = new { type = "plain_text", text = "Select a decision" },
                        options = new object[]
                        {
                            new
                            {
                                text = new { type = "plain_text", text = "Approve" },
                                value = "approve",
                            },
                            new
                            {
                                text = new { type = "plain_text", text = "Request changes" },
                                value = "request-changes",
                            },
                            new
                            {
                                text = new { type = "plain_text", text = "Reject" },
                                value = "reject",
                            },
                        },
                    },
                },
                new
                {
                    type = "input",
                    block_id = "review_comment",
                    label = new { type = "plain_text", text = "Comment" },
                    optional = true,
                    element = new
                    {
                        type = "plain_text_input",
                        action_id = "review_comment_input",
                        multiline = true,
                    },
                },
            },
        };
    }

    /// <inheritdoc />
    public object RenderEscalateModal(SlackEscalateModalContext context)
    {
        // Encode questionId in private_metadata (= TaskId for
        // task-level escalations) so the Stage 5.3 view-submission
        // handler can construct HumanDecisionEvent without falling
        // back to a TaskId alias.
        //
        // The escalate modal's static_select (severity) carries the
        // ESCALATION URGENCY tier, not the bare verdict -- the
        // verdict for an "escalate" view_submission is always the
        // literal "escalate" (there is no approve / reject choice).
        // actionValue is therefore pinned in private_metadata so the
        // handler reads metadata.ActionValue as the verdict half.
        //
        // SEVERITY PROPAGATION (Stage 6.1 implemented; iter 5 doc
        // refresh per evaluator item 4): the
        // SlackInteractionHandler composes
        // HumanDecisionEvent.ActionValue as
        // "<pinned>{EscalateSeveritySeparator}<severity>" --
        // i.e. "escalate:critical", "escalate:warning", or
        // "escalate:info". HumanDecisionEvent has no Metadata slot
        // (architecture.md §3.6.3 pins eight fields), so this
        // namespace-on-ActionValue encoding is the typed-decision
        // surface for severity. Downstream consumers can match the
        // bare verdict with ActionValue.StartsWith("escalate") and
        // the urgency tier with the suffix after the separator. The
        // handler's composition is idempotent: a pinned base that
        // already contains the separator (e.g. a hand-rolled modal
        // pinning "escalate:warning") is not appended a second time.
        // Slack always echoes initial_option on a submit without
        // touching the select, so production submissions always
        // carry one of the three severity values.
        string privateMetadata = SerializeEscalatePrivateMetadata(
            questionId: context.TaskId,
            taskId: context.TaskId,
            subCommand: "escalate",
            actionValue: EscalateActionValue,
            correlationId: context.CorrelationId);

        return new
        {
            type = "modal",
            callback_id = EscalateCallbackId,
            private_metadata = privateMetadata,
            title = new { type = "plain_text", text = BuildModalTitle("Escalate", context.TaskId) },
            submit = new { type = "plain_text", text = "Escalate" },
            close = new { type = "plain_text", text = "Cancel" },
            blocks = new object[]
            {
                new
                {
                    type = "section",
                    block_id = "escalate_context",
                    text = new
                    {
                        type = "mrkdwn",
                        text = $"*Escalation* for task `{context.TaskId}` requested by <@{context.UserId}> in <#{context.ChannelId}>",
                    },
                },
                new
                {
                    // Implementation-plan.md Stage 6.1 step 6 explicitly
                    // requires a severity select on the escalate modal so
                    // the operator can pick the urgency tier (critical /
                    // warning / info) when raising the escalation. The
                    // chosen value lands in view.state.values keyed on
                    // EscalateSeverityBlockId / EscalateSeveritySelectActionId
                    // and is composed into HumanDecisionEvent.ActionValue
                    // by SlackInteractionHandler as
                    // "escalate:<severity>" (see SEVERITY PROPAGATION
                    // note in the method-level comment above).
                    type = "input",
                    block_id = EscalateSeverityBlockId,
                    label = new { type = "plain_text", text = "Severity" },
                    element = new
                    {
                        type = "static_select",
                        action_id = EscalateSeveritySelectActionId,
                        placeholder = new { type = "plain_text", text = "Pick a severity" },
                        initial_option = new
                        {
                            text = new { type = "plain_text", text = "Warning" },
                            value = SeverityWarning,
                        },
                        options = new object[]
                        {
                            new
                            {
                                text = new { type = "plain_text", text = "Critical" },
                                value = SeverityCritical,
                            },
                            new
                            {
                                text = new { type = "plain_text", text = "Warning" },
                                value = SeverityWarning,
                            },
                            new
                            {
                                text = new { type = "plain_text", text = "Info" },
                                value = SeverityInfo,
                            },
                        },
                    },
                },
                new
                {
                    // The escalate modal carries EXACTLY ONE
                    // plain_text_input (the reason). Stage 5.3's
                    // SlackInteractionHandler.ParseView walks
                    // view.state.values in block order and assigns
                    // FirstPlainTextInputValue -- which is then
                    // surfaced as HumanDecisionEvent.Comment -- so any
                    // additional plain_text_input added here would
                    // race with reason for that slot and silently
                    // overwrite the escalation justification. The
                    // brief (implementation-plan.md Stage 6.1 step 6)
                    // requires task context + severity + reason +
                    // submit/cancel, period; we DELIBERATELY do NOT
                    // render an optional "target" input.
                    type = "input",
                    block_id = EscalateReasonBlockId,
                    label = new { type = "plain_text", text = "Reason" },
                    element = new
                    {
                        type = "plain_text_input",
                        action_id = EscalateReasonInputActionId,
                        multiline = true,
                    },
                },
            },
        };
    }

    /// <summary>
    /// <c>block_id</c> Stage 5.3 uses for the comment modal's text input
    /// block. The interaction handler walks <c>view.state.values</c>
    /// looking for this block to read the typed comment.
    /// </summary>
    public const string CommentBlockId = "agent_comment";

    /// <summary>
    /// <c>action_id</c> of the plain-text input inside
    /// <see cref="CommentBlockId"/>.
    /// </summary>
    public const string CommentInputActionId = "agent_comment_input";

    /// <inheritdoc />
    public object RenderCommentModal(SlackCommentModalContext context)
    {
        string privateMetadata = SerializeCommentPrivateMetadata(context);

        string titleSuffix = string.IsNullOrEmpty(context.ActionLabel)
            ? context.QuestionId
            : context.ActionLabel;
        string headlineLabel = string.IsNullOrEmpty(context.ActionLabel)
            ? context.ActionValue
            : context.ActionLabel;

        return new
        {
            type = "modal",
            callback_id = SlackInteractionEncoding.CommentCallbackId,
            private_metadata = privateMetadata,
            title = new { type = "plain_text", text = BuildModalTitle("Comment", titleSuffix) },
            submit = new { type = "plain_text", text = "Submit" },
            close = new { type = "plain_text", text = "Cancel" },
            blocks = new object[]
            {
                new
                {
                    type = "section",
                    block_id = "comment_context",
                    text = new
                    {
                        type = "mrkdwn",
                        text = $"*{headlineLabel}* on question `{context.QuestionId}` from <@{context.UserId}>. Add a comment to record your reasoning.",
                    },
                },
                new
                {
                    type = "input",
                    block_id = CommentBlockId,
                    label = new { type = "plain_text", text = "Comment" },
                    element = new
                    {
                        type = "plain_text_input",
                        action_id = CommentInputActionId,
                        multiline = true,
                    },
                },
            },
        };
    }

    private static string BuildModalTitle(string action, string taskId)
    {
        // Slack caps view titles at 24 chars. "{Action} {TaskId}" is the
        // most useful form within that budget; we truncate the task-id
        // tail with an ellipsis when the combined length would overflow.
        const int slackTitleMax = 24;
        string baseTitle = $"{action} {taskId}";
        if (baseTitle.Length <= slackTitleMax)
        {
            return baseTitle;
        }

        int budget = slackTitleMax - (action.Length + 2);
        if (budget <= 0)
        {
            return action;
        }

        return $"{action} {taskId[..budget]}…";
    }

    private static string SerializePrivateMetadata(string questionId, string taskId, string subCommand, string correlationId)
        => JsonSerializer.Serialize(new
        {
            questionId,
            taskId,
            subCommand,
            correlationId,
        });

    /// <summary>
    /// <c>actionValue</c> pinned into the escalate modal's
    /// <c>private_metadata</c> so the view-submission handler reads a
    /// verdict via <c>metadata.ActionValue</c> instead of falling back
    /// to a raw text-input value. The escalate modal's static_select
    /// carries the SEVERITY (escalation-routing context); the verdict
    /// for an escalate view_submission is always the literal
    /// <c>"escalate"</c>, never a severity tier.
    /// </summary>
    public const string EscalateActionValue = "escalate";

    /// <summary>
    /// <c>block_id</c> Stage 6.1 uses for the escalate modal's
    /// severity static_select. On submission Slack exposes the
    /// chosen value at
    /// <c>view.state.values[escalate_severity][escalate_severity_select].selected_option.value</c>.
    /// The Stage 5.3 <see cref="Pipeline.SlackInteractionHandler"/>
    /// then composes
    /// <c>HumanDecisionEvent.ActionValue = "{pinned}{EscalateSeveritySeparator}{severity}"</c>
    /// (e.g. <c>"escalate:critical"</c>), which is the typed-decision
    /// surface for the severity tier because
    /// <c>HumanDecisionEvent</c> has no <c>Metadata</c> slot
    /// (architecture.md §3.6.3 pins eight fields). The composition is
    /// idempotent: if the pinned base already contains the separator
    /// the handler does NOT append a second time. Downstream consumers
    /// route on the bare verdict with
    /// <c>ActionValue.StartsWith("escalate")</c> and on the urgency
    /// with the suffix after the separator.
    /// </summary>
    public const string EscalateSeverityBlockId = "escalate_severity";

    /// <summary>
    /// <c>action_id</c> of the static_select inside
    /// <see cref="EscalateSeverityBlockId"/>.
    /// </summary>
    public const string EscalateSeveritySelectActionId = "escalate_severity_select";

    /// <summary>
    /// <c>block_id</c> Stage 6.1 uses for the escalate modal's
    /// multi-line reason input.
    /// </summary>
    public const string EscalateReasonBlockId = "escalate_reason";

    /// <summary>
    /// <c>action_id</c> of the plain-text input inside
    /// <see cref="EscalateReasonBlockId"/>.
    /// </summary>
    public const string EscalateReasonInputActionId = "escalate_reason_input";

    private static string SerializeEscalatePrivateMetadata(
        string questionId,
        string taskId,
        string subCommand,
        string actionValue,
        string correlationId)
        => JsonSerializer.Serialize(new
        {
            questionId,
            taskId,
            subCommand,
            actionValue,
            correlationId,
        });

    private static string SerializeCommentPrivateMetadata(SlackCommentModalContext context)
        => JsonSerializer.Serialize(new
        {
            questionId = context.QuestionId,
            actionValue = context.ActionValue,
            actionLabel = context.ActionLabel,
            teamId = context.TeamId,
            channelId = context.ChannelId,
            messageTs = context.MessageTs,
            threadTs = context.ThreadTs,
            userId = context.UserId,
            correlationId = context.CorrelationId,
        });

    // -----------------------------------------------------------------
    // Stage 6.1: Block Kit message rendering
    // -----------------------------------------------------------------

    /// <summary>
    /// Slack hard-caps a Block Kit message at 50 blocks
    /// (tech-spec.md §5.2). The renderer must stay strictly under this
    /// limit so the dispatcher never round-trips an invalid payload.
    /// </summary>
    public const int MaxBlocksPerMessage = 50;

    /// <summary>
    /// Slack hard-caps any single Block Kit <c>text</c> field at 3000
    /// characters (tech-spec.md §5.2). Values above the cap are
    /// truncated with <see cref="TruncationIndicator"/>.
    /// </summary>
    public const int MaxTextFieldLength = 3000;

    /// <summary>
    /// Slack allows at most 5 elements (buttons) per <c>actions</c>
    /// block. Buttons beyond this count are split across additional
    /// actions blocks.
    /// </summary>
    public const int MaxButtonsPerActionsBlock = 5;

    /// <summary>
    /// Slack caps a <c>header</c> block's plain text at 150 characters.
    /// </summary>
    public const int MaxHeaderTextLength = 150;

    /// <summary>
    /// Ellipsis appended to a <c>text</c> field that hits
    /// <see cref="MaxTextFieldLength"/>. Selected so the resulting
    /// string is bit-for-bit 3000 characters (the 1-char "…" replaces
    /// the final character of the source rather than appending past
    /// the cap).
    /// </summary>
    public const string TruncationIndicator = "…";

    /// <summary>
    /// Severity strings recognised by
    /// <see cref="RenderQuestion(AgentQuestion)"/>. Comparison is
    /// case-insensitive (ordinal); unrecognised values map to
    /// <see cref="DefaultSeverityKey"/>.
    /// </summary>
    public const string SeverityCritical = "critical";

    /// <inheritdoc cref="SeverityCritical"/>
    public const string SeverityWarning = "warning";

    /// <inheritdoc cref="SeverityCritical"/>
    public const string SeverityInfo = "info";

    /// <summary>Severity key used when the supplied value does not match a known bucket.</summary>
    public const string DefaultSeverityKey = SeverityInfo;

    private static readonly IReadOnlyDictionary<string, SeverityStyle> SeverityStyles =
        new Dictionary<string, SeverityStyle>(StringComparer.OrdinalIgnoreCase)
        {
            [SeverityCritical] = new("🔴", "#dc3545"),
            [SeverityWarning] = new("🟡", "#ffc107"),
            [SeverityInfo] = new("🔵", "#0d6efd"),
        };

    private static readonly IReadOnlyDictionary<MessageType, MessageTypeStyle> MessageTypeStyles =
        new Dictionary<MessageType, MessageTypeStyle>
        {
            [MessageType.StatusUpdate] = new("ℹ️", "#0d6efd"),
            [MessageType.Completion] = new("✅", "#28a745"),
            [MessageType.Error] = new("❌", "#dc3545"),
            [MessageType.Unspecified] = new(string.Empty, "#6c757d"),
        };

    /// <inheritdoc />
    public object RenderQuestion(AgentQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        SeverityStyle severityStyle = ResolveSeverityStyle(question.Severity);
        List<object> headerBlocks = new(capacity: 3);

        string headerText = Truncate($"{severityStyle.Emoji} {question.Title}".Trim(), MaxHeaderTextLength);
        headerBlocks.Add(new
        {
            type = "header",
            text = new
            {
                type = "plain_text",
                text = headerText,
                emoji = true,
            },
        });

        headerBlocks.Add(new
        {
            type = "section",
            block_id = "question_body",
            text = new
            {
                type = "mrkdwn",
                text = Truncate(question.Body ?? string.Empty, MaxTextFieldLength),
            },
        });

        // Stage 6.1 evaluator item 2 (iter 5): the expiry context block
        // MUST always be rendered, even when an overflowing payload
        // forces EnforceBlockLimit to drop trailing action blocks.
        // Building the expiry context AS PART OF the always-preserved
        // tail (passed to EnforceBlockLimit) guarantees the deadline
        // is visible even on payloads with hundreds of actions.
        object expiryContext = new
        {
            type = "context",
            block_id = "question_expiry",
            elements = new object[]
            {
                new
                {
                    type = "mrkdwn",
                    text = $":hourglass_flowing_sand: Expires at {FormatExpiry(question.ExpiresAt)}",
                },
            },
        };

        List<object> actionBlocks = new();
        if (question.AllowedActions is { Count: > 0 } actions)
        {
            // Stage 6.1 evaluator item 1 (iter 5): walk actions in
            // CALLER ORDER, opening a new actions block whenever the
            // RequiresComment flag flips OR the current chunk hits
            // Slack's MaxButtonsPerActionsBlock cap. The previous
            // GroupBy(a => a.RequiresComment) implementation rearranged
            // buttons so all RequiresComment=false rendered first --
            // an input order like approve / reject-with-comment /
            // defer would have rendered approve / defer / reject-with-comment.
            // Adjacent-grouping preserves the caller's intent.
            //
            // The SlackInteractionEncoding contract bakes RequiresComment
            // into the block_id prefix (q: vs qc:), so adjacent groups
            // with differing flags MUST live in separate actions blocks.
            // Chunk indexes are tracked PER FLAG VALUE so block_ids
            // stay unique even when the same flag appears in multiple
            // non-adjacent groups (e.g. no / yes / no produces
            // q:Q, qc:Q, qk:1:Q -- three Slack-unique block_ids that
            // all decode back to the same QuestionId).
            int chunkIndexFalse = 0;
            int chunkIndexTrue = 0;
            int i = 0;
            while (i < actions.Count)
            {
                bool flag = actions[i].RequiresComment;
                int chunkStart = i;
                int chunkEnd = i;
                while (chunkEnd < actions.Count
                    && actions[chunkEnd].RequiresComment == flag
                    && (chunkEnd - chunkStart) < MaxButtonsPerActionsBlock)
                {
                    chunkEnd++;
                }

                int chunkIndex = flag ? chunkIndexTrue++ : chunkIndexFalse++;
                string blockId = SlackInteractionEncoding.EncodeQuestionBlockId(
                    question.QuestionId, flag, chunkIndex);

                HumanAction[] chunk = new HumanAction[chunkEnd - chunkStart];
                for (int k = 0; k < chunk.Length; k++)
                {
                    chunk[k] = actions[chunkStart + k];
                }

                actionBlocks.Add(BuildActionsBlock(blockId, chunk));
                i = chunkEnd;
            }
        }

        List<object> assembled = new(capacity: headerBlocks.Count + actionBlocks.Count + 1);
        assembled.AddRange(headerBlocks);
        assembled.AddRange(actionBlocks);

        // Pass the expiry context as a required tail so EnforceBlockLimit
        // preserves it even when the action blocks overflow the 50-block
        // cap. The truncation marker (when emitted) sits BETWEEN the
        // kept prefix blocks and the preserved tail.
        object[] blockArray = EnforceBlockLimit(assembled, new[] { expiryContext });

        // Wrap blocks in a legacy attachment whose `color` paints the
        // severity sidebar -- architecture.md §2.10 line 189:
        // "Severity -> emoji prefix and color attachment bar".
        return new
        {
            attachments = new object[]
            {
                new
                {
                    color = severityStyle.Color,
                    blocks = blockArray,
                },
            },
        };
    }

    /// <inheritdoc />
    public object RenderMessage(MessengerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        MessageTypeStyle style = ResolveMessageTypeStyle(message.MessageType);
        string rawContent = message.Content ?? string.Empty;
        string prefix = string.IsNullOrEmpty(style.Emoji) ? string.Empty : style.Emoji + " ";

        // Slack caps a single `text` field at MaxTextFieldLength (3000).
        // Reserve room for the emoji prefix BEFORE truncation so the
        // prefixed string also stays inside the cap.
        int contentBudget = Math.Max(1, MaxTextFieldLength - prefix.Length);
        string text = prefix + Truncate(rawContent, contentBudget);

        object[] blockArray = EnforceBlockLimit(
            new List<object>
            {
                new
                {
                    type = "section",
                    block_id = "message_body",
                    text = new
                    {
                        type = "mrkdwn",
                        text = text,
                    },
                },
            },
            requiredTail: null);

        return new
        {
            attachments = new object[]
            {
                new
                {
                    color = style.Color,
                    blocks = blockArray,
                },
            },
        };
    }

    private static object BuildActionsBlock(string blockId, IReadOnlyList<HumanAction> actions)
    {
        object[] elements = new object[actions.Count];
        for (int i = 0; i < actions.Count; i++)
        {
            HumanAction action = actions[i];
            elements[i] = new
            {
                type = "button",
                action_id = action.ActionId,
                text = new
                {
                    type = "plain_text",
                    text = Truncate(action.Label ?? string.Empty, 75),
                    emoji = true,
                },
                value = action.Value,
            };
        }

        return new
        {
            type = "actions",
            block_id = blockId,
            elements = elements,
        };
    }

    /// <summary>
    /// Caps <paramref name="blocks"/> + <paramref name="requiredTail"/>
    /// at <see cref="MaxBlocksPerMessage"/>. When the combined input
    /// would overflow, drops the tail of <paramref name="blocks"/>,
    /// appends a <c>context</c> block marking the truncation, then
    /// appends the <paramref name="requiredTail"/> blocks (which are
    /// ALWAYS preserved -- callers use this slot for blocks the brief
    /// pins as MUST-be-rendered, e.g. the expiry context).
    /// </summary>
    /// <remarks>
    /// Stage 6.1 evaluator item 2 (iter 5): the expiry context block
    /// for <see cref="RenderQuestion"/> is passed as
    /// <paramref name="requiredTail"/> so a high-action payload never
    /// drops the <c>ExpiresAt</c> deadline. Pre-iter-5 the renderer
    /// appended the context to the main block list, which meant a
    /// 250-action question with 53 blocks lost the expiry context
    /// when EnforceBlockLimit trimmed the tail.
    /// </remarks>
    private static object[] EnforceBlockLimit(List<object> blocks, IReadOnlyList<object>? requiredTail)
    {
        int tailCount = requiredTail?.Count ?? 0;
        int totalCount = blocks.Count + tailCount;

        if (totalCount <= MaxBlocksPerMessage)
        {
            object[] combined = new object[totalCount];
            for (int i = 0; i < blocks.Count; i++)
            {
                combined[i] = blocks[i];
            }

            for (int i = 0; i < tailCount; i++)
            {
                combined[blocks.Count + i] = requiredTail![i];
            }

            return combined;
        }

        // Budget = MaxBlocksPerMessage - tailCount - 1 (1 for marker).
        // Keep that many prefix blocks from `blocks`, then the marker,
        // then the full requiredTail. tailCount + 1 must be < cap for
        // this to make sense; defensive clamp at minimum of 0.
        int prefixBudget = Math.Max(0, MaxBlocksPerMessage - tailCount - 1);
        int omittedFromPrefix = blocks.Count - prefixBudget;

        object[] truncated = new object[MaxBlocksPerMessage];
        for (int i = 0; i < prefixBudget; i++)
        {
            truncated[i] = blocks[i];
        }

        truncated[prefixBudget] = new
        {
            type = "context",
            block_id = "blocks_truncated",
            elements = new object[]
            {
                new
                {
                    type = "mrkdwn",
                    text = $":warning: Message truncated -- {omittedFromPrefix} block(s) omitted to stay within Slack's {MaxBlocksPerMessage}-block limit.",
                },
            },
        };

        for (int i = 0; i < tailCount; i++)
        {
            truncated[prefixBudget + 1 + i] = requiredTail![i];
        }

        return truncated;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        if (maxLength <= TruncationIndicator.Length)
        {
            return value[..maxLength];
        }

        int sliceLength = maxLength - TruncationIndicator.Length;
        return value[..sliceLength] + TruncationIndicator;
    }

    private static SeverityStyle ResolveSeverityStyle(string? severity)
    {
        if (!string.IsNullOrEmpty(severity)
            && SeverityStyles.TryGetValue(severity, out SeverityStyle style))
        {
            return style;
        }

        return SeverityStyles[DefaultSeverityKey];
    }

    private static MessageTypeStyle ResolveMessageTypeStyle(MessageType type)
    {
        return MessageTypeStyles.TryGetValue(type, out MessageTypeStyle style)
            ? style
            : MessageTypeStyles[MessageType.Unspecified];
    }

    private static string FormatExpiry(DateTimeOffset expiresAt)
        => expiresAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private readonly record struct SeverityStyle(string Emoji, string Color);

    private readonly record struct MessageTypeStyle(string Emoji, string Color);
}
