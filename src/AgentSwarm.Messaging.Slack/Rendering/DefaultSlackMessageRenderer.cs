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
/// Iter-2 evaluator items 2 + 4 fix: the Stage 4.1
/// <c>DefaultSlackModalPayloadBuilder</c> only stored
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
        string privateMetadata = SerializePrivateMetadata(
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
                                value = "request_changes",
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
        string privateMetadata = SerializePrivateMetadata(
            taskId: context.TaskId,
            subCommand: "escalate",
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

    private static string SerializePrivateMetadata(string taskId, string subCommand, string correlationId)
        => JsonSerializer.Serialize(new
        {
            taskId,
            subCommand,
            correlationId,
        });
}
