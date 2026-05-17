// -----------------------------------------------------------------------
// <copyright file="ISlackChatPostMessageClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stage 6.2 thread-aware <c>chat.postMessage</c> seam used by
/// <see cref="SlackThreadManager{TContext}"/> to create root messages
/// for new agent task threads and to re-create the thread in a
/// fallback channel when the original conversation is no longer
/// reachable. Returns the structured outcome (created <c>ts</c>,
/// Slack error string, HTTP status) so the manager can branch on
/// <c>channel_not_found</c> / <c>is_archived</c> / <c>not_in_channel</c>
/// / <c>message_not_found</c> and trigger fallback recovery
/// (architecture.md §2.11 lifecycle step 4).
/// </summary>
/// <remarks>
/// <para>
/// The contract is intentionally distinct from
/// <see cref="ISlackThreadedReplyPoster"/>: the Stage 5.2 reply poster
/// is fire-and-forget (no <c>ts</c> capture, errors swallowed) and is
/// used by the @-mention handler. The thread manager needs the
/// returned <c>ts</c> (it becomes the thread anchor) AND the Slack
/// error code so it can choose between persisting the mapping,
/// falling back to <see cref="Entities.SlackWorkspaceConfig.FallbackChannelId"/>,
/// or surfacing the failure to the caller. Sharing a single client
/// would force one of those callers to compromise.
/// </para>
/// <para>
/// Stage 6.4's <c>SlackDirectApiClient</c> is expected to supersede
/// this client once the consolidated SlackNet-backed Web API client
/// lands. Until then, <see cref="HttpClientSlackChatPostMessageClient"/>
/// is the production binding.
/// </para>
/// </remarks>
internal interface ISlackChatPostMessageClient
{
    /// <summary>
    /// POSTs <paramref name="request"/> to Slack's
    /// <c>chat.postMessage</c> endpoint. Returns the outcome; throws
    /// ONLY for caller cancellation.
    /// </summary>
    Task<SlackChatPostMessageResult> PostAsync(
        SlackChatPostMessageRequest request,
        CancellationToken ct);
}

/// <summary>
/// Input bundle for
/// <see cref="ISlackChatPostMessageClient.PostAsync"/>.
/// <see cref="ThreadTs"/> is <see langword="null"/> for ROOT messages
/// (top-level posts that establish a new thread anchor) and set to the
/// owning thread's <c>ts</c> for THREADED replies posted by Stage 6.2's
/// <c>SlackThreadManager.PostThreadedReplyAsync</c>. The client passes
/// the value through verbatim to Slack's <c>chat.postMessage</c>
/// endpoint (the <c>thread_ts</c> field is omitted when null so root
/// posts behave as documented).
/// </summary>
/// <param name="TeamId">Slack workspace id; used to resolve the per-workspace
/// bot OAuth token via
/// <see cref="Security.ISlackWorkspaceConfigStore"/> +
/// <see cref="Core.Secrets.ISecretProvider"/>.</param>
/// <param name="ChannelId">Slack channel id to post the message into.</param>
/// <param name="Text">Plain-text body (Slack markdown-lite is honoured).
/// Callers MUST stay within Slack's 3000-character chat.postMessage
/// limit; the client does not truncate.</param>
/// <param name="CorrelationId">End-to-end correlation id surfaced in the
/// implementation's log lines so operators can match the post against
/// the originating audit row.</param>
/// <param name="ThreadTs">When non-null, posted as the <c>thread_ts</c>
/// payload field so Slack threads the message under the owning root.
/// <see langword="null"/> for root messages.</param>
internal readonly record struct SlackChatPostMessageRequest(
    string TeamId,
    string ChannelId,
    string Text,
    string CorrelationId,
    string? ThreadTs = null);

/// <summary>
/// Outcome of an <see cref="ISlackChatPostMessageClient.PostAsync"/>
/// call. <see cref="Ts"/> and <see cref="Channel"/> are populated when
/// <see cref="Kind"/> is <see cref="SlackChatPostMessageResultKind.Ok"/>;
/// <see cref="Error"/> carries the Slack-reported error string (e.g.
/// <c>channel_not_found</c>) on a <see cref="SlackChatPostMessageResultKind.SlackError"/>
/// result.
/// </summary>
/// <param name="Kind">Discriminator for the outcome.</param>
/// <param name="Ts">Slack timestamp (<c>ts</c>) of the created message,
/// or <see langword="null"/> when the post did not succeed.</param>
/// <param name="Channel">Slack channel id Slack echoes back on a
/// successful post, or <see langword="null"/> otherwise.</param>
/// <param name="Error">Slack-reported error string when
/// <see cref="Kind"/> is <see cref="SlackChatPostMessageResultKind.SlackError"/>;
/// a free-text diagnostic for the other failure kinds.</param>
internal readonly record struct SlackChatPostMessageResult(
    SlackChatPostMessageResultKind Kind,
    string? Ts,
    string? Channel,
    string? Error)
{
    /// <summary>Convenience accessor: true when the post succeeded.</summary>
    public bool IsSuccess => this.Kind == SlackChatPostMessageResultKind.Ok;

    /// <summary>
    /// True when the Slack-reported error indicates the previously-stored
    /// channel / thread is gone (deleted root message, archived channel,
    /// bot evicted from channel). The thread manager uses this to decide
    /// whether to attempt fallback recovery.
    /// </summary>
    public bool IsChannelMissing
        => this.Kind == SlackChatPostMessageResultKind.SlackError
            && this.Error is not null
            && (string.Equals(this.Error, "channel_not_found", System.StringComparison.Ordinal)
                || string.Equals(this.Error, "is_archived", System.StringComparison.Ordinal)
                || string.Equals(this.Error, "not_in_channel", System.StringComparison.Ordinal)
                || string.Equals(this.Error, "message_not_found", System.StringComparison.Ordinal)
                || string.Equals(this.Error, "thread_not_found", System.StringComparison.Ordinal));

    public static SlackChatPostMessageResult Success(string ts, string? channel)
        => new(SlackChatPostMessageResultKind.Ok, ts, channel, null);

    public static SlackChatPostMessageResult Failure(string error)
        => new(SlackChatPostMessageResultKind.SlackError, null, null, error);

    public static SlackChatPostMessageResult NetworkFailure(string error)
        => new(SlackChatPostMessageResultKind.NetworkFailure, null, null, error);

    public static SlackChatPostMessageResult MissingConfiguration(string error)
        => new(SlackChatPostMessageResultKind.MissingConfiguration, null, null, error);

    public static SlackChatPostMessageResult Skipped(string reason)
        => new(SlackChatPostMessageResultKind.Skipped, null, null, reason);
}

/// <summary>Discriminator on <see cref="SlackChatPostMessageResult"/>.</summary>
internal enum SlackChatPostMessageResultKind
{
    /// <summary>Slack accepted the post and returned a <c>ts</c>.</summary>
    Ok = 0,

    /// <summary>Slack accepted the HTTP request but returned <c>{"ok":false, "error":"..."}</c>.</summary>
    SlackError = 1,

    /// <summary>Transport-layer error (timeout, DNS, TLS, 5xx).</summary>
    NetworkFailure = 2,

    /// <summary>Workspace lacks a bot-token secret reference or the secret resolves to empty.</summary>
    MissingConfiguration = 3,

    /// <summary>Client refused to issue the call (e.g., empty channel id).</summary>
    Skipped = 4,
}
