// -----------------------------------------------------------------------
// <copyright file="DefaultSlackMessageRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Rendering;

using System.Text.Json;

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
        // The escalate modal is a TEXT-ONLY view (target + reason
        // inputs), so there is no static_select that the handler can
        // read as ActionValue. Without a pinned actionValue the
        // handler's fallback chain would degrade to the first raw
        // plain_text_input value (the user's free-form "escalate to"
        // text), producing a HumanDecisionEvent whose ActionValue is
        // meaningless data rather than a verdict. Pin
        // actionValue = EscalateActionValue so the handler reads
        // metadata.ActionValue and the contract is explicit: an
        // "escalate" view_submission always publishes a
        // HumanDecisionEvent with ActionValue = "escalate".
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
                    type = "input",
                    block_id = "escalate_target",
                    label = new { type = "plain_text", text = "Escalate to (user or group)" },
                    element = new
                    {
                        type = "plain_text_input",
                        action_id = "escalate_target_input",
                        placeholder = new { type = "plain_text", text = "@user or @group" },
                    },
                },
                new
                {
                    type = "input",
                    block_id = "escalate_reason",
                    label = new { type = "plain_text", text = "Reason" },
                    element = new
                    {
                        type = "plain_text_input",
                        action_id = "escalate_reason_input",
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
    /// to a raw text-input value. The escalate modal is text-only --
    /// there is no static_select to read from
    /// <c>view.state.values</c>.
    /// </summary>
    public const string EscalateActionValue = "escalate";

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
}
