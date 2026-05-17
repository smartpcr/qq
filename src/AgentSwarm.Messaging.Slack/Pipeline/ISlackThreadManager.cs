// -----------------------------------------------------------------------
// <copyright file="ISlackThreadManager.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Manages the one-to-one mapping between agent tasks and Slack threads.
/// Creates the root message (thread parent) on first outbound message
/// for a task, stores the mapping in
/// <see cref="SlackThreadMapping"/>, retrieves the thread anchor for
/// subsequent replies, posts THREADED replies to the owning thread, and
/// recovers into the workspace's
/// <see cref="SlackWorkspaceConfig.FallbackChannelId"/> when the original
/// channel is no longer reachable.
/// </summary>
/// <remarks>
/// <para>
/// Stage 6.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Architecture.md §2.11 (lifecycle) and §4.3 (interface). The
/// implementation owned by this stage is
/// <see cref="SlackThreadManager{TContext}"/>; Stage 6.3's outbound
/// dispatcher is the primary consumer.
/// </para>
/// <para>
/// <b>Channel resolution.</b> The manager is the single source of truth
/// for "which channel does a task's thread live in". Callers do NOT
/// supply a channel; the manager resolves it from the persisted
/// workspace configuration:
/// <list type="bullet">
///   <item><description>New thread → workspace
///     <see cref="SlackWorkspaceConfig.DefaultChannelId"/>.</description></item>
///   <item><description>Root post fails with a Slack
///     "channel missing" error (deleted root, archived channel,
///     <c>channel_not_found</c>, <c>not_in_channel</c>) AND the
///     workspace has a
///     <see cref="SlackWorkspaceConfig.FallbackChannelId"/> → retry on
///     the fallback channel (architecture.md §2.11 lifecycle step 4,
///     e2e-scenarios.md scenario 13.3).</description></item>
/// </list>
/// This change relative to architecture §4.3 (which lists a
/// caller-supplied <c>channelId</c>) is deliberate: Stage 6.2's
/// implementation-plan step explicitly says "post a root status message
/// to the workspace's <c>DefaultChannelId</c>", and centralising the
/// resolution prevents callers from accidentally bypassing
/// <see cref="SlackWorkspaceConfig.AllowedChannelIds"/> by hand-picking
/// a channel id.
/// </para>
/// <para>
/// <b>Deviation from architecture §4.3 -- extra parameters.</b> The
/// published signature shape includes a <c>channelId</c> and lacks
/// <c>teamId</c>; this contract drops <c>channelId</c> (see above) and
/// adds <c>teamId</c> because
/// <see cref="SlackThreadMapping.TeamId"/> is a required column on the
/// persisted row and the underlying <c>chat.postMessage</c> client
/// resolves the bot OAuth token via
/// <see cref="Security.ISlackWorkspaceConfigStore"/> keyed by
/// <c>team_id</c>. Stage 6.3's dispatcher always has the team-id in
/// scope (it derives from the upstream task / connector binding) so the
/// extra parameter is free at the call site.
/// </para>
/// </remarks>
internal interface ISlackThreadManager
{
    /// <summary>
    /// Returns the existing <see cref="SlackThreadMapping"/> for
    /// <paramref name="taskId"/>; when none exists, resolves the
    /// workspace's <see cref="SlackWorkspaceConfig.DefaultChannelId"/>,
    /// posts a root status message via <c>chat.postMessage</c>,
    /// captures the returned <c>ts</c> as
    /// <see cref="SlackThreadMapping.ThreadTs"/>, persists the mapping,
    /// and returns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inline fallback.</b> If the initial root post fails with a
    /// recoverable Slack error (<c>channel_not_found</c>,
    /// <c>is_archived</c>, <c>not_in_channel</c>, etc.) AND the
    /// workspace has a
    /// <see cref="SlackWorkspaceConfig.FallbackChannelId"/> configured,
    /// the manager retries the root post against that fallback channel
    /// before throwing. The resulting mapping is persisted with the
    /// fallback channel as <see cref="SlackThreadMapping.ChannelId"/>
    /// and an outbound audit row with outcome <c>fallback_used</c> is
    /// emitted. Implements architecture.md §2.11 lifecycle step 4 and
    /// satisfies e2e-scenarios.md scenario 13.3 directly through the
    /// <c>GetOrCreateThreadAsync</c> path so callers do not need a
    /// separate recovery branch.
    /// </para>
    /// <para>
    /// Throws <see cref="SlackThreadCreationException"/> when no usable
    /// channel can be reached (workspace unknown, no default channel,
    /// initial post fails with a non-recoverable error and either no
    /// fallback is configured or the fallback post also fails). Stage
    /// 6.3's outbound dispatcher catches this and routes the originating
    /// outbound message back through retry / dead-letter.
    /// </para>
    /// </remarks>
    /// <param name="taskId">Agent task identifier (mapping primary key). Required.</param>
    /// <param name="agentId">Owning agent identifier; stored on the mapping.</param>
    /// <param name="correlationId">End-to-end correlation id; stored on the mapping
    /// and surfaced in the outbound audit row so an operator can query every
    /// agent/human exchange by correlation id (FR-004).</param>
    /// <param name="teamId">Slack workspace identifier; used both to resolve the
    /// per-workspace bot OAuth token and to populate
    /// <see cref="SlackThreadMapping.TeamId"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SlackThreadMapping> GetOrCreateThreadAsync(
        string taskId,
        string agentId,
        string correlationId,
        string teamId,
        CancellationToken ct);

    /// <summary>
    /// Looks up the <see cref="SlackThreadMapping"/> for
    /// <paramref name="taskId"/>, returning <see langword="null"/>
    /// when no row exists.
    /// </summary>
    Task<SlackThreadMapping?> GetThreadAsync(string taskId, CancellationToken ct);

    /// <summary>
    /// Bumps <see cref="SlackThreadMapping.LastMessageAt"/> on the
    /// mapping for <paramref name="taskId"/> to the current UTC time.
    /// Returns <see langword="false"/> when no mapping exists for
    /// <paramref name="taskId"/>; the caller MAY use this to detect an
    /// unexpectedly-evicted mapping but is not required to.
    /// </summary>
    /// <remarks>
    /// Stage 6.2 implementation step 6 (update <c>LastMessageAt</c> on
    /// every new message posted to the thread) is satisfied implicitly
    /// by <see cref="PostThreadedReplyAsync"/>, which bumps the column
    /// on every successful send. <see cref="TouchAsync"/> exists so
    /// callers that post threaded messages through a different path
    /// (e.g. Stage 5.2's <see cref="ISlackThreadedReplyPoster"/>) can
    /// surface the activity to the freshness column without having to
    /// take a hard dependency on the EF-backed concrete type.
    /// </remarks>
    Task<bool> TouchAsync(string taskId, CancellationToken ct);

    /// <summary>
    /// Recovers the thread for <paramref name="taskId"/> after the
    /// previously-stored channel / thread is no longer reachable
    /// (deleted root message, archived channel,
    /// <c>channel_not_found</c>, etc.): posts a new root message in
    /// the workspace's
    /// <see cref="SlackWorkspaceConfig.FallbackChannelId"/>, overwrites
    /// the mapping's channel and <c>thread_ts</c>, and emits a
    /// <c>thread_recover</c> outbound audit row. Returns
    /// <see langword="null"/> when no fallback channel is configured or
    /// the recovery post itself fails -- in which case the caller is
    /// responsible for surfacing the failure (typically by routing the
    /// outbound message to the dead-letter queue).
    /// </summary>
    /// <remarks>
    /// <see cref="GetOrCreateThreadAsync"/> already invokes the
    /// fallback path inline when the very first root post fails; this
    /// dedicated entry point exists for the second-hop case (the
    /// previously-persisted mapping points at a now-archived channel
    /// and a threaded REPLY fails). Stage 6.3's outbound dispatcher
    /// calls this when an in-thread message fails with a Slack
    /// "channel missing" error.
    /// </remarks>
    /// <param name="taskId">Agent task identifier.</param>
    /// <param name="agentId">Owning agent identifier.</param>
    /// <param name="correlationId">End-to-end correlation id.</param>
    /// <param name="teamId">Slack workspace identifier; used to resolve
    /// <see cref="SlackWorkspaceConfig.FallbackChannelId"/> and the bot
    /// OAuth token.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SlackThreadMapping?> RecoverThreadAsync(
        string taskId,
        string agentId,
        string correlationId,
        string teamId,
        CancellationToken ct);

    /// <summary>
    /// Posts a THREADED reply for <paramref name="taskId"/>: resolves
    /// the persisted <see cref="SlackThreadMapping"/>, calls
    /// <c>chat.postMessage</c> with <c>thread_ts</c> set so the message
    /// lands inside the owning thread, bumps
    /// <see cref="SlackThreadMapping.LastMessageAt"/> on success, and
    /// emits an outbound audit row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the canonical production path that satisfies Stage 6.2's
    /// implementation step 6 ("Update <c>LastMessageAt</c> on every new
    /// message posted to the thread"). Routing every in-thread send
    /// through the manager (rather than letting callers reach the chat
    /// client directly) guarantees the bump cannot be silently skipped.
    /// </para>
    /// <para>
    /// When the Slack response indicates the persisted channel / thread
    /// is gone (<c>channel_not_found</c>, <c>is_archived</c>,
    /// <c>message_not_found</c>, <c>thread_not_found</c>,
    /// <c>not_in_channel</c>), the manager attempts inline fallback
    /// recovery: posts a fresh root on the workspace's
    /// <see cref="SlackWorkspaceConfig.FallbackChannelId"/>, rewrites
    /// the mapping, audits the recovery, and then re-posts the reply
    /// into the new thread. The return value reports either
    /// <see cref="SlackThreadPostStatus.Posted"/> or
    /// <see cref="SlackThreadPostStatus.Recovered"/> on success so the
    /// caller can surface the recovery to the operator.
    /// </para>
    /// </remarks>
    /// <param name="taskId">Agent task identifier whose thread should receive the reply.</param>
    /// <param name="text">Plain-text reply body (Slack mrkdwn honoured).</param>
    /// <param name="correlationId">End-to-end correlation id; included
    /// in the outbound audit row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SlackThreadPostResult> PostThreadedReplyAsync(
        string taskId,
        string text,
        string? correlationId,
        CancellationToken ct);
}

/// <summary>
/// Outcome of <see cref="ISlackThreadManager.PostThreadedReplyAsync"/>.
/// <see cref="MessageTs"/> is populated on success;
/// <see cref="Error"/> carries a diagnostic string on failure.
/// </summary>
/// <param name="Status">Discriminator for the outcome.</param>
/// <param name="Mapping">The mapping the reply was posted against
/// (post-recovery for <see cref="SlackThreadPostStatus.Recovered"/>),
/// or <see langword="null"/> when no mapping was found.</param>
/// <param name="MessageTs">Slack timestamp (<c>ts</c>) of the reply
/// message, or <see langword="null"/> when the reply did not succeed.</param>
/// <param name="Error">Free-text diagnostic when <see cref="Status"/> is
/// a failure variant; <see langword="null"/> on success.</param>
internal readonly record struct SlackThreadPostResult(
    SlackThreadPostStatus Status,
    SlackThreadMapping? Mapping,
    string? MessageTs,
    string? Error)
{
    /// <summary>True for <see cref="SlackThreadPostStatus.Posted"/> /
    /// <see cref="SlackThreadPostStatus.Recovered"/>.</summary>
    public bool IsSuccess
        => this.Status == SlackThreadPostStatus.Posted
            || this.Status == SlackThreadPostStatus.Recovered;

    public static SlackThreadPostResult Posted(SlackThreadMapping mapping, string messageTs)
        => new(SlackThreadPostStatus.Posted, mapping, messageTs, null);

    public static SlackThreadPostResult Recovered(SlackThreadMapping mapping, string messageTs)
        => new(SlackThreadPostStatus.Recovered, mapping, messageTs, null);

    public static SlackThreadPostResult MappingMissing(string taskId)
        => new(SlackThreadPostStatus.MappingMissing, null, null,
            $"no thread mapping exists for task_id='{taskId}'.");

    public static SlackThreadPostResult Failed(SlackThreadMapping? mapping, string error)
        => new(SlackThreadPostStatus.Failed, mapping, null, error);
}

/// <summary>Discriminator on <see cref="SlackThreadPostResult"/>.</summary>
internal enum SlackThreadPostStatus
{
    /// <summary>The reply was posted into the originally-persisted thread.</summary>
    Posted = 0,

    /// <summary>The reply was posted, but only after the originally-persisted
    /// channel/thread was found to be archived and the thread was
    /// re-created on the workspace's
    /// <see cref="SlackWorkspaceConfig.FallbackChannelId"/>.</summary>
    Recovered = 1,

    /// <summary>No <see cref="SlackThreadMapping"/> exists for the supplied task id.</summary>
    MappingMissing = 2,

    /// <summary>The post failed and could not be recovered.</summary>
    Failed = 3,
}
