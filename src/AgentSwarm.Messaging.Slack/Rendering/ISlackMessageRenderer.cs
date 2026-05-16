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
