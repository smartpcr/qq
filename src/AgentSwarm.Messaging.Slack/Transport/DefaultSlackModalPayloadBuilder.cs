// -----------------------------------------------------------------------
// <copyright file="DefaultSlackModalPayloadBuilder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;

/// <summary>
/// Stage 4.1 default modal payload builder. Produces the minimal
/// Block Kit payload that satisfies the fast-path acceptance
/// criterion ("Human can answer via button or modal") today, while
/// Stage 5.2's <c>SlackMessageRenderer</c> takes over the richer
/// rendering responsibilities.
/// </summary>
internal sealed class DefaultSlackModalPayloadBuilder : ISlackModalPayloadBuilder
{
    /// <inheritdoc />
    public object BuildView(string subCommand, SlackInboundEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrEmpty(subCommand);
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        string normalized = subCommand.Trim().ToLowerInvariant();
        return normalized switch
        {
            "review" => this.BuildReviewView(envelope),
            "escalate" => this.BuildEscalateView(envelope),
            _ => throw new ArgumentOutOfRangeException(
                nameof(subCommand),
                subCommand,
                "Default modal payload builder only handles 'review' and 'escalate'."),
        };
    }

    private object BuildReviewView(SlackInboundEnvelope envelope) => new
    {
        type = "modal",
        callback_id = "agent_review_modal",
        private_metadata = envelope.IdempotencyKey,
        title = new { type = "plain_text", text = "Review Request" },
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
                    text = $"*Review request* from <@{envelope.UserId}> in <#{envelope.ChannelId}>",
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

    private object BuildEscalateView(SlackInboundEnvelope envelope) => new
    {
        type = "modal",
        callback_id = "agent_escalate_modal",
        private_metadata = envelope.IdempotencyKey,
        title = new { type = "plain_text", text = "Escalate" },
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
                    text = $"*Escalation* requested by <@{envelope.UserId}> in <#{envelope.ChannelId}>",
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
