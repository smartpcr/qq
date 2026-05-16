// -----------------------------------------------------------------------
// <copyright file="ISlackThreadedReplyPoster.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Posts a plain-text reply into a Slack channel as a threaded message
/// via <c>chat.postMessage</c>. Used by the Stage 5.2
/// <see cref="SlackAppMentionHandler"/> to deliver handler responses
/// (acknowledgements, status output, usage hints, fall-back hints for
/// review / escalate) into the same channel / thread where the
/// originating <c>@AgentBot</c> mention was posted.
/// </summary>
/// <remarks>
/// <para>
/// Stage 5.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// "Post handler responses as threaded replies in the channel where the
/// mention occurred (using the message's <c>thread_ts</c> if already in
/// a thread, or creating a new thread if in the main channel)".
/// </para>
/// <para>
/// Slack's slash-command <c>response_url</c> mechanism is unavailable
/// for Events API <c>app_mention</c> payloads (those events do not
/// carry a per-invocation response URL); the only Slack-supported path
/// for replying is <c>chat.postMessage</c> with a <c>thread_ts</c>
/// targeting either the existing containing thread or the mention's
/// own <c>ts</c> (which Slack will promote into a new thread).
/// </para>
/// <para>
/// The default registration is
/// <see cref="NoOpSlackThreadedReplyPoster"/>, which logs at
/// <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/> and
/// returns. The Stage 6.x outbound dispatcher will swap in a real
/// HTTP-backed implementation that resolves the per-workspace bot
/// OAuth token via <c>ISlackWorkspaceConfigStore</c> +
/// <c>ISecretProvider</c> and POSTs to
/// <c>https://slack.com/api/chat.postMessage</c>; the dedicated
/// abstraction keeps the Stage 5.2 wiring decoupled from that
/// later-stage outbound rate-limit + retry plumbing so the handler can
/// be tested in isolation today.
/// </para>
/// <para>
/// Implementations MUST swallow non-fatal HTTP errors (rate-limit
/// responses, transient 5xx) and log them; a missed threaded reply is
/// recoverable from logs, but turning it into a thrown exception would
/// dead-letter an otherwise-correct orchestrator dispatch (the
/// orchestrator-side <see cref="AgentSwarm.Messaging.Abstractions.IAgentTaskService"/>
/// has already run by the time this is called). Implementations DO
/// throw for <see cref="System.OperationCanceledException"/> so the
/// dispatch loop can honour shutdown.
/// </para>
/// </remarks>
internal interface ISlackThreadedReplyPoster
{
    /// <summary>
    /// Posts <paramref name="request"/>.<see cref="SlackThreadedReplyRequest.Text"/>
    /// into the supplied channel / thread.
    /// </summary>
    Task PostAsync(SlackThreadedReplyRequest request, CancellationToken ct);
}

/// <summary>
/// Input bundle for
/// <see cref="ISlackThreadedReplyPoster.PostAsync"/>. Encapsulates the
/// destination routing (workspace + channel + optional thread anchor)
/// and the rendered text body. The Stage 5.2 handler computes
/// <see cref="ThreadTs"/> as the <c>event.thread_ts</c> when the
/// mention was posted inside an existing thread, falling back to the
/// mention's own <c>event.ts</c> when the mention was a top-level
/// channel post -- Slack will promote the latter into a new thread on
/// first reply.
/// </summary>
/// <param name="TeamId">Slack workspace id; the implementation uses
/// this to resolve the workspace's bot OAuth token.</param>
/// <param name="ChannelId">Slack channel id where the reply should be
/// posted. MUST NOT be null or empty.</param>
/// <param name="ThreadTs">Thread anchor. Stage 5.2 always supplies a
/// non-empty value; <see langword="null"/> is reserved for future
/// callers that want to post a top-level channel message.</param>
/// <param name="Text">Plain-text body (Slack markdown-lite is
/// honoured). Callers MUST stay within Slack's 3000-character
/// chat.postMessage limit; the responder does not truncate.</param>
/// <param name="CorrelationId">End-to-end correlation id (usually the
/// inbound envelope's idempotency key). Surfaced in the implementation's
/// log lines so operators can match the post against the originating
/// audit row.</param>
internal readonly record struct SlackThreadedReplyRequest(
    string TeamId,
    string ChannelId,
    string? ThreadTs,
    string Text,
    string CorrelationId);
