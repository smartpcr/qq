// -----------------------------------------------------------------------
// <copyright file="ISlackMessageRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Rendering;

using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Stage 5.1+ presentation surface: renders the Slack
/// <c>view</c> JSON payloads (and, when Stage 5.2 ships, the Block Kit
/// message blocks for app-mention responses) consumed by every Slack
/// connector code path -- the synchronous modal fast-path
/// (<see cref="DefaultSlackModalFastPathHandler"/>) AND the async
/// slash-command dispatcher
/// (<see cref="Pipeline.SlackCommandHandler"/>).
/// </summary>
/// <remarks>
/// <para>
/// The Stage 4.1 <see cref="ISlackModalPayloadBuilder"/> shipped a
/// minimal-but-real Block Kit modal builder that did not carry the
/// task-id supplied by <c>/agent review &lt;task-id&gt;</c> /
/// <c>/agent escalate &lt;task-id&gt;</c> into the rendered view's
/// <c>private_metadata</c>; the Stage 5.3 view-submission handler
/// therefore had no way to map a submitted modal back to the agent task
/// the human intended to review. Stage 5.1 introduces this renderer with
/// a task-id-aware context so the modal carries the task-id forward in
/// both <c>private_metadata</c> (machine-readable) and the visible
/// title (so the human reviewer also sees what they are acting on).
/// </para>
/// <para>
/// Stage 5.2 extends this renderer with methods for the threaded
/// <c>chat.postMessage</c> bodies the orchestrator emits in response to
/// <c>/agent ask</c> and to subsequent agent updates. Until then the
/// renderer's surface is intentionally narrow (review / escalate
/// modals), and the implementation is internal.
/// </para>
/// </remarks>
internal interface ISlackMessageRenderer
{
    /// <summary>
    /// Renders the Slack <c>view</c> JSON for the
    /// <c>/agent review &lt;task-id&gt;</c> modal. The returned object
    /// is serialised by the <c>views.open</c> Web API call.
    /// </summary>
    object RenderReviewModal(SlackReviewModalContext context);

    /// <summary>
    /// Renders the Slack <c>view</c> JSON for the
    /// <c>/agent escalate &lt;task-id&gt;</c> modal.
    /// </summary>
    object RenderEscalateModal(SlackEscalateModalContext context);

    /// <summary>
    /// Renders the Slack <c>view</c> JSON for the follow-up
    /// "comment required" modal opened by the Stage 5.3
    /// <see cref="Pipeline.SlackInteractionHandler"/> when a clicked
    /// Block Kit button's backing
    /// <see cref="AgentSwarm.Messaging.Abstractions.HumanAction.RequiresComment"/>
    /// is <see langword="true"/>. The submitted view is decoded by the
    /// same handler into a
    /// <see cref="AgentSwarm.Messaging.Abstractions.HumanDecisionEvent"/>
    /// whose <c>ActionValue</c> is pinned from the originating button
    /// (carried through <c>private_metadata</c>) and whose
    /// <c>Comment</c> is read from the text input.
    /// </summary>
    object RenderCommentModal(SlackCommentModalContext context);
}

/// <summary>
/// Input bundle for <see cref="ISlackMessageRenderer.RenderReviewModal"/>.
/// </summary>
/// <param name="TaskId">Orchestrator-assigned task identifier supplied
/// as the argument to <c>/agent review &lt;task-id&gt;</c>. Carried into
/// the modal's <c>private_metadata</c> so the Stage 5.3 view-submission
/// handler can correlate the submitted decision back to its task.</param>
/// <param name="TeamId">Slack workspace id (audit / authorisation context).</param>
/// <param name="ChannelId">Slack channel id (may be empty for
/// workspace-level invocations).</param>
/// <param name="UserId">Slack user id of the human invoking the
/// command (rendered in the modal so reviewers see who requested it).</param>
/// <param name="CorrelationId">End-to-end correlation id; usually
/// derived from the inbound envelope's idempotency key so Slack retries
/// land in the same audit row.</param>
internal readonly record struct SlackReviewModalContext(
    string TaskId,
    string TeamId,
    string? ChannelId,
    string UserId,
    string CorrelationId);

/// <summary>
/// Input bundle for <see cref="ISlackMessageRenderer.RenderEscalateModal"/>.
/// </summary>
/// <param name="TaskId">Orchestrator-assigned task identifier supplied
/// as the argument to <c>/agent escalate &lt;task-id&gt;</c>.</param>
/// <param name="TeamId">Slack workspace id.</param>
/// <param name="ChannelId">Slack channel id (may be empty).</param>
/// <param name="UserId">Slack user id of the human escalating.</param>
/// <param name="CorrelationId">End-to-end correlation id.</param>
internal readonly record struct SlackEscalateModalContext(
    string TaskId,
    string TeamId,
    string? ChannelId,
    string UserId,
    string CorrelationId);

/// <summary>
/// Input bundle for <see cref="ISlackMessageRenderer.RenderCommentModal"/>.
/// </summary>
/// <param name="QuestionId">Originating
/// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion.QuestionId"/>
/// carried forward from the clicked button's <c>block_id</c>.</param>
/// <param name="ActionValue">Machine-readable value of the originating
/// <see cref="AgentSwarm.Messaging.Abstractions.HumanAction"/>
/// (the button's <c>value</c>) pinned so the submitted modal can
/// reconstruct the
/// <see cref="AgentSwarm.Messaging.Abstractions.HumanDecisionEvent.ActionValue"/>.</param>
/// <param name="ActionLabel">Display label of the originating action;
/// used as the modal title to give the human visual confirmation of
/// what they are commenting on.</param>
/// <param name="TeamId">Slack workspace id (audit / routing).</param>
/// <param name="ChannelId">Slack channel id of the parent message
/// (may be <see langword="null"/> for workspace-level interactions).</param>
/// <param name="MessageTs">Slack <c>message.ts</c> of the parent
/// message so the submission can reach the same
/// <see cref="Entities.SlackThreadMapping"/> when resolving
/// <c>CorrelationId</c>.</param>
/// <param name="ThreadTs">Slack <c>message.thread_ts</c> of the
/// parent message (the root timestamp of the conversation thread
/// the click landed in). Pinned so the
/// <see cref="Pipeline.SlackInteractionHandler"/>'s view_submission
/// path resolves the same <c>SlackThreadMapping</c> row the
/// originating button click did. <see langword="null"/> when the
/// click was on the thread's root message (Slack omits
/// <c>thread_ts</c> in that case and <see cref="MessageTs"/> IS the
/// root). Stage 5.3 evaluator iter-2 item #1.</param>
/// <param name="UserId">Slack user id of the human who clicked the
/// originating button.</param>
/// <param name="CorrelationId">End-to-end correlation id carried
/// forward so the eventual decision lands in the same audit row.</param>
internal readonly record struct SlackCommentModalContext(
    string QuestionId,
    string ActionValue,
    string ActionLabel,
    string TeamId,
    string? ChannelId,
    string MessageTs,
    string? ThreadTs,
    string UserId,
    string CorrelationId);
