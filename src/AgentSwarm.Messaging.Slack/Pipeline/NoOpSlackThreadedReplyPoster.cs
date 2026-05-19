// -----------------------------------------------------------------------
// <copyright file="NoOpSlackThreadedReplyPoster.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 5.2 default <see cref="ISlackThreadedReplyPoster"/>. Logs the
/// would-be threaded reply at <see cref="LogLevel.Warning"/> and
/// completes synchronously so the
/// <see cref="SlackAppMentionHandler"/> remains resolvable in
/// composition roots that have not yet wired the Stage 6.x
/// HTTP-backed <c>chat.postMessage</c> client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Do NOT deploy this to production.</b> The no-op completes
/// without invoking Slack, so a user who @-mentions the bot will see
/// no reply at all -- the orchestrator-side side-effect (e.g.
/// <c>CreateTaskAsync</c>) still runs because the
/// <see cref="SlackAppMentionHandler"/> calls
/// <see cref="SlackCommandHandler.DispatchAsync"/> before the responder
/// fires, but the human-visible acknowledgement is lost.
/// </para>
/// <para>
/// Modelled on <see cref="NoOpAgentTaskService"/>: the warning log
/// makes an accidental production deployment observable and points
/// at the registration that needs to be replaced. Stage 6.x's
/// outbound dispatcher swaps in the real implementation via DI; the
/// dispatcher uses <c>TryAddSingleton</c> for its registration so a
/// host-supplied production poster registered earlier wins.
/// </para>
/// </remarks>
internal sealed class NoOpSlackThreadedReplyPoster : ISlackThreadedReplyPoster
{
    private readonly ILogger<NoOpSlackThreadedReplyPoster> logger;

    public NoOpSlackThreadedReplyPoster(ILogger<NoOpSlackThreadedReplyPoster> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task PostAsync(SlackThreadedReplyRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        this.logger.LogWarning(
            "NoOpSlackThreadedReplyPoster swallowed a threaded reply request for team_id={TeamId} channel_id={ChannelId} thread_ts={ThreadTs} correlation_id={CorrelationId}. The Stage 6.x outbound dispatcher MUST replace this default before production. Suppressed text: '{Text}'.",
            request.TeamId,
            request.ChannelId,
            request.ThreadTs,
            request.CorrelationId,
            request.Text);
        return Task.CompletedTask;
    }
}
