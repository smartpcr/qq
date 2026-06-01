// -----------------------------------------------------------------------
// <copyright file="SlackModalPayload.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Stage 6.4 typed input for
/// <see cref="SlackDirectApiClient.OpenModalAsync"/>. Carries the
/// per-workspace token-resolution key (<see cref="TeamId"/>), the
/// pre-rendered Slack <c>view</c> JSON (<see cref="View"/>), and the
/// optional audit-context fields the direct client uses to write the
/// <c>request_type = modal_open</c> audit row (architecture.md §2.15,
/// implementation-plan.md Stage 6.4 step 4).
/// </summary>
/// <remarks>
/// <para>
/// The legacy in-memory anonymous-object surface used by Stage 4.1's
/// <see cref="HttpClientSlackViewsOpenClient"/> remains accessible
/// through <see cref="ISlackViewsOpenClient.OpenAsync"/> (which
/// <see cref="SlackDirectApiClient"/> also implements). The
/// <see cref="SlackModalPayload"/> exists so callers that own the
/// modal-open lifecycle end-to-end can hand
/// <see cref="SlackDirectApiClient"/> everything it needs to (a) call
/// Slack's <c>views.open</c> via SlackNet, (b) acquire the shared
/// rate-limit token, AND (c) emit an audit row -- all in a single
/// synchronous call from the HTTP request lifecycle.
/// </para>
/// <para>
/// Audit fields are optional: callers that lack a correlation id /
/// user id / channel id may leave them <see langword="null"/> and
/// <see cref="SlackDirectApiClient"/> will write the audit row with
/// the sensible defaults documented on <see cref="Entities.SlackAuditEntry"/>.
/// </para>
/// </remarks>
/// <param name="TeamId">Slack workspace identifier (<c>team_id</c>)
/// used to resolve the per-workspace bot OAuth token via
/// <see cref="Security.ISlackWorkspaceConfigStore"/> +
/// <see cref="Core.Secrets.ISecretProvider"/> AND as the
/// per-workspace rate-limiter scope key (<c>views.open</c> is a
/// Tier&#160;4, per-workspace call per architecture.md §2.12).</param>
/// <param name="View">Pre-rendered Slack <c>view</c> JSON. Typically
/// the anonymous-object tree returned by
/// <see cref="Rendering.ISlackMessageRenderer.RenderReviewModal"/> /
/// <see cref="Rendering.ISlackMessageRenderer.RenderEscalateModal"/>.
/// SlackNet serialises it as the <c>view</c> argument of the Web API
/// call.</param>
internal sealed record SlackModalPayload(string TeamId, object View)
{
    /// <summary>
    /// End-to-end correlation id propagated from the originating
    /// inbound envelope (the slash command's idempotency key). Used as
    /// the <see cref="Entities.SlackAuditEntry.CorrelationId"/> on the
    /// transport audit row so the modal_open call is queryable
    /// alongside the originating command per FR-004.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Slack user id of the human invoking the modal-opening command,
    /// stamped onto the audit row's
    /// <see cref="Entities.SlackAuditEntry.UserId"/>.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Slack channel id the modal was triggered from. May be
    /// <see langword="null"/> for workspace-level invocations. Stamped
    /// onto the audit row's
    /// <see cref="Entities.SlackAuditEntry.ChannelId"/>.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Lower-case sub-command name (<c>review</c> / <c>escalate</c>)
    /// used to build the audit row's
    /// <see cref="Entities.SlackAuditEntry.CommandText"/>
    /// (<c>/agent {SubCommand}</c>). When <see langword="null"/> the
    /// audit row's command text is left empty.
    /// </summary>
    public string? SubCommand { get; init; }
}

/// <summary>
/// Result of <see cref="SlackDirectApiClient.OpenModalAsync"/>. Carries
/// the same discriminator surface as <see cref="SlackViewsOpenResult"/>
/// (for callers that already branch on
/// <see cref="SlackViewsOpenResultKind"/>) PLUS a pre-formatted
/// <see cref="EphemeralMessage"/> the controller can hand straight
/// back to Slack as the slash-command response body on failure -- a
/// concession to the brief's requirement that the modal fast-path
/// surfaces an ephemeral error to the invoking user when
/// <c>views.open</c> fails (architecture.md §2.15, implementation-plan
/// Stage 6.4 step 5).
/// </summary>
/// <param name="Kind">Discriminator: <see cref="SlackViewsOpenResultKind.Ok"/>
/// on success; one of the failure values otherwise.</param>
/// <param name="Error">Slack-reported error code (e.g.,
/// <c>expired_trigger_id</c>, <c>rate_limited</c>) on a Slack-side
/// failure; a free-text diagnostic for other failure kinds; <c>null</c>
/// on success.</param>
/// <param name="EphemeralMessage">User-facing ephemeral message text on
/// failure; <see langword="null"/> on success. The
/// <c>SlackCommandsController</c> wraps this in
/// <c>{"response_type":"ephemeral","text":"..."}</c> when surfacing
/// the failure to Slack.</param>
internal readonly record struct SlackDirectApiResult(
    SlackViewsOpenResultKind Kind,
    string? Error,
    string? EphemeralMessage)
{
    /// <summary>True when <see cref="Kind"/> is
    /// <see cref="SlackViewsOpenResultKind.Ok"/>.</summary>
    public bool IsSuccess => this.Kind == SlackViewsOpenResultKind.Ok;

    /// <summary>Builds the success singleton.</summary>
    public static SlackDirectApiResult Success() => new(SlackViewsOpenResultKind.Ok, null, null);

    /// <summary>Builds a failure result with the supplied user-facing message.</summary>
    public static SlackDirectApiResult Failure(SlackViewsOpenResultKind kind, string? error, string ephemeralMessage)
        => new(kind, error, ephemeralMessage);
}
