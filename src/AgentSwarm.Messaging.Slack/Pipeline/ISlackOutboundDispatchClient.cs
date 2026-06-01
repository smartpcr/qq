// -----------------------------------------------------------------------
// <copyright file="ISlackOutboundDispatchClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Internal Slack Web API seam used by
/// <see cref="SlackOutboundDispatcher"/>. Issues the three POST-only
/// verbs the durable outbound queue carries -- <c>chat.postMessage</c>,
/// <c>chat.update</c>, <c>views.update</c> -- and surfaces every
/// outcome (success, HTTP 429 + <c>Retry-After</c>, transient /
/// permanent failure, missing configuration) so the dispatcher can
/// branch without re-parsing HTTP details.
/// </summary>
/// <remarks>
/// <para>
/// Stage 6.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 1 + step 6. Stage 6.4's <c>SlackDirectApiClient</c> will
/// eventually consolidate this seam together with the
/// <c>views.open</c> fast-path; until then the dispatcher uses this
/// distinct client so the queue-driven dispatch loop remains testable
/// without spinning up the modal fast-path's secret resolution + ACK
/// budgeting.
/// </para>
/// <para>
/// All Slack-reported failure modes are RETURNED, never thrown -- a
/// thrown <see cref="System.OperationCanceledException"/> on shutdown
/// is the sole exception that escapes <see cref="DispatchAsync"/>.
/// This keeps the dispatcher loop's catch-block tight and ensures a
/// transient Slack 5xx does not bubble all the way to the
/// <c>BackgroundService</c> boundary.
/// </para>
/// </remarks>
internal interface ISlackOutboundDispatchClient
{
    /// <summary>
    /// Sends <paramref name="request"/> to Slack. Returns the
    /// outcome; throws ONLY when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<SlackOutboundDispatchResult> DispatchAsync(
        SlackOutboundDispatchRequest request,
        CancellationToken ct);
}

/// <summary>
/// Resolved Slack Web API call carried into
/// <see cref="ISlackOutboundDispatchClient.DispatchAsync"/>. The
/// dispatcher constructs this from the dequeued
/// <see cref="SlackOutboundEnvelope"/> and the resolved
/// <see cref="Entities.SlackThreadMapping"/>.
/// </summary>
/// <param name="Operation">Slack Web API verb to invoke.</param>
/// <param name="TeamId">Slack workspace id (used to resolve the bot OAuth token).</param>
/// <param name="ChannelId">Slack channel id the message lives in. Required for
/// <see cref="SlackOutboundOperationKind.PostMessage"/> and
/// <see cref="SlackOutboundOperationKind.UpdateMessage"/>; may be
/// empty for <see cref="SlackOutboundOperationKind.ViewsUpdate"/>.</param>
/// <param name="ThreadTs">Slack thread timestamp to post into (post-message)
/// or update inside (update-message). <see langword="null"/> for
/// root posts -- the dispatcher only uses this path on the very
/// first message a connector sends.</param>
/// <param name="MessageTs">Existing message timestamp targeted by
/// <see cref="SlackOutboundOperationKind.UpdateMessage"/>; for
/// post-message and views.update calls this is <see langword="null"/>.</param>
/// <param name="ViewId">Slack view id targeted by
/// <see cref="SlackOutboundOperationKind.ViewsUpdate"/>; ignored by
/// the message verbs.</param>
/// <param name="BlockKitPayload">Pre-rendered Slack message body (JSON
/// produced by <see cref="Rendering.ISlackMessageRenderer"/>). For
/// post-message / update-message the dispatcher merges <c>channel</c>
/// and (when applicable) <c>thread_ts</c>/<c>ts</c> into the payload
/// before POSTing; for views.update the payload is the view object.</param>
/// <param name="CorrelationId">End-to-end correlation id for log / audit lines.</param>
internal readonly record struct SlackOutboundDispatchRequest(
    SlackOutboundOperationKind Operation,
    string TeamId,
    string ChannelId,
    string? ThreadTs,
    string? MessageTs,
    string? ViewId,
    string BlockKitPayload,
    string CorrelationId);

/// <summary>
/// Classified outcome of a single <see cref="ISlackOutboundDispatchClient.DispatchAsync"/>
/// call.
/// </summary>
/// <param name="Outcome">Discriminator -- the dispatcher branches on
/// this value (success/return, 429/pause, transient/retry,
/// permanent/dead-letter, missing-configuration/dead-letter).</param>
/// <param name="HttpStatusCode">HTTP status code Slack returned, or
/// <c>0</c> when no HTTP exchange completed (transport failure).</param>
/// <param name="SlackError">Slack-reported <c>{"ok":false,"error":...}</c>
/// string, or a free-text diagnostic for the non-Slack failure
/// kinds; <see langword="null"/> on success.</param>
/// <param name="RetryAfter">Duration from the response's
/// <c>Retry-After</c> header (parsed as either seconds or HTTP-date);
/// non-null only when <see cref="Outcome"/> is
/// <see cref="SlackOutboundDispatchOutcome.RateLimited"/>.</param>
/// <param name="ResponsePayload">Raw response body Slack returned;
/// captured for the outbound audit row. <see langword="null"/> for
/// transport-only failures.</param>
/// <param name="MessageTs">Slack <c>ts</c> of the created or updated
/// message (post-message returns the new ts; update-message echoes
/// the targeted ts). <see langword="null"/> for views.update and
/// failure outcomes.</param>
internal readonly record struct SlackOutboundDispatchResult(
    SlackOutboundDispatchOutcome Outcome,
    int HttpStatusCode,
    string? SlackError,
    TimeSpan? RetryAfter,
    string? ResponsePayload,
    string? MessageTs)
{
    /// <summary>True when <see cref="Outcome"/> is <see cref="SlackOutboundDispatchOutcome.Success"/>.</summary>
    public bool IsSuccess => this.Outcome == SlackOutboundDispatchOutcome.Success;

    public static SlackOutboundDispatchResult Success(int statusCode, string? messageTs, string? responsePayload) =>
        new(SlackOutboundDispatchOutcome.Success, statusCode, null, null, responsePayload, messageTs);

    public static SlackOutboundDispatchResult RateLimited(int statusCode, TimeSpan retryAfter, string? responsePayload) =>
        new(SlackOutboundDispatchOutcome.RateLimited, statusCode, "rate_limited", retryAfter, responsePayload, null);

    public static SlackOutboundDispatchResult Transient(int statusCode, string error, string? responsePayload) =>
        new(SlackOutboundDispatchOutcome.TransientFailure, statusCode, error, null, responsePayload, null);

    public static SlackOutboundDispatchResult Permanent(int statusCode, string error, string? responsePayload) =>
        new(SlackOutboundDispatchOutcome.PermanentFailure, statusCode, error, null, responsePayload, null);

    public static SlackOutboundDispatchResult MissingConfiguration(string error) =>
        new(SlackOutboundDispatchOutcome.MissingConfiguration, 0, error, null, null, null);
}

/// <summary>
/// Discriminator on <see cref="SlackOutboundDispatchResult"/>.
/// </summary>
internal enum SlackOutboundDispatchOutcome
{
    /// <summary>The call succeeded and Slack returned <c>{"ok":true}</c>.</summary>
    Success = 0,

    /// <summary>HTTP 429 -- the dispatcher pauses the bucket per
    /// <see cref="SlackOutboundDispatchResult.RetryAfter"/> and retries.</summary>
    RateLimited = 1,

    /// <summary>Transient HTTP 5xx / network error / <c>{"ok":false}</c>
    /// with a Slack error that is documented as retryable.</summary>
    TransientFailure = 2,

    /// <summary>HTTP 4xx (other than 429) or a non-retryable Slack
    /// error (e.g. <c>invalid_blocks</c>, <c>channel_not_found</c>,
    /// <c>token_revoked</c>) -- the dispatcher dead-letters without
    /// retrying.</summary>
    PermanentFailure = 3,

    /// <summary>The workspace lacks a usable bot-token secret reference
    /// or the secret resolved to empty. Dead-lettered immediately
    /// because no retry can recover.</summary>
    MissingConfiguration = 4,
}
