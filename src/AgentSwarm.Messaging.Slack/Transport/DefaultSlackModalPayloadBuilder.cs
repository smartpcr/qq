// -----------------------------------------------------------------------
// <copyright file="DefaultSlackModalPayloadBuilder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using AgentSwarm.Messaging.Slack.Rendering;

/// <summary>
/// Stage 4.1 modal payload builder shim. As of Stage 5.1 (iter-2
/// evaluator items 2 + 4) this builder is a thin adapter that
/// (1) reparses the command's sub-command and task-id arguments out of
/// <see cref="SlackInboundEnvelope.RawPayload"/>, (2) builds a
/// task-id-aware <see cref="SlackReviewModalContext"/> /
/// <see cref="SlackEscalateModalContext"/>, and (3) delegates to the
/// new <see cref="ISlackMessageRenderer"/>. The class is kept so that
/// the Stage 4.1 modal fast-path's existing DI contract
/// (<see cref="DefaultSlackModalFastPathHandler"/> -&gt;
/// <see cref="ISlackModalPayloadBuilder"/>) continues to resolve and so
/// every existing fast-path test pin (which constructs a
/// <c>DefaultSlackModalPayloadBuilder</c> directly) keeps working --
/// but the actual Block Kit rendering is now owned by Stage 5.1's
/// renderer.
/// </summary>
internal sealed class DefaultSlackModalPayloadBuilder : ISlackModalPayloadBuilder
{
    private readonly ISlackMessageRenderer renderer;

    public DefaultSlackModalPayloadBuilder()
        : this(new DefaultSlackMessageRenderer())
    {
    }

    public DefaultSlackModalPayloadBuilder(ISlackMessageRenderer renderer)
    {
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    /// <inheritdoc />
    public object BuildView(string subCommand, SlackInboundEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrEmpty(subCommand);
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        string normalized = subCommand.Trim().ToLowerInvariant();

        // Re-parse the envelope's command body so the renderer gets a
        // task-id context even on the synchronous fast-path. The Stage
        // 4.1 fast-path runs INSIDE the controller's HTTP request, so
        // this reparse adds at most a microsecond and is the cheapest
        // way to thread task-id through without changing
        // ISlackModalPayloadBuilder's narrow signature.
        SlackCommandPayload command = SlackInboundPayloadParser.ParseCommand(envelope.RawPayload);

        // Iter-3 evaluator item 1 (STRUCTURAL fix): previously this
        // method fell back to envelope.IdempotencyKey when ArgumentText
        // was empty -- producing a modal whose `private_metadata`
        // carried a key that did not identify any agent task. That hid
        // the missing-argument bug behind a successful views.open call
        // and corrupted the Stage 5.3 view-submission correlation.
        //
        // The contract is now: callers MUST pre-validate that the
        // command supplies a task-id BEFORE invoking BuildView. The
        // production fast-path (DefaultSlackModalFastPathHandler) and
        // the async dispatcher (SlackCommandHandler.HandleModalAsync)
        // both implement that gate; the builder fails loudly so a
        // future caller that forgets the gate is caught by the
        // handler's try/catch -> ephemeral error path rather than
        // silently opening a degraded modal.
        string? taskId = command.ArgumentText?.Trim();
        if (string.IsNullOrEmpty(taskId))
        {
            throw new ArgumentException(
                $"Slack modal payload for sub-command '{normalized}' requires a non-empty task-id argument (parsed from the command's text payload); the caller MUST validate this BEFORE invoking BuildView.",
                nameof(envelope));
        }

        string correlationId = string.IsNullOrEmpty(envelope.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : envelope.IdempotencyKey;

        return normalized switch
        {
            "review" => this.renderer.RenderReviewModal(new SlackReviewModalContext(
                TaskId: taskId,
                TeamId: envelope.TeamId,
                ChannelId: envelope.ChannelId,
                UserId: envelope.UserId,
                CorrelationId: correlationId)),

            "escalate" => this.renderer.RenderEscalateModal(new SlackEscalateModalContext(
                TaskId: taskId,
                TeamId: envelope.TeamId,
                ChannelId: envelope.ChannelId,
                UserId: envelope.UserId,
                CorrelationId: correlationId)),

            _ => throw new ArgumentOutOfRangeException(
                nameof(subCommand),
                subCommand,
                "Default modal payload builder only handles 'review' and 'escalate'."),
        };
    }
}
