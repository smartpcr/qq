// -----------------------------------------------------------------------------
// AgentSwarm.Messaging.Slack
// Stage: security-pipeline / authorization-filter-and-membership-resolution
//
// SlackAuthorizationFilter runs immediately after SlackSignatureMiddleware in
// the security pipeline. By the time this filter is invoked the request body
// has been HMAC-validated against a single workspace's signing secret, and a
// SlackSignatureIdentity has been stamped onto HttpContext.Items containing:
//
//   * identity.TeamId    -- the team_id keyed off the signing secret used to
//                           verify the request. This is the only team id that
//                           is cryptographically attested.
//   * identity.Workspace -- the workspace record looked up during signature
//                           verification (may be null on non-event endpoints
//                           where the middleware ran in passive mode).
//
// The filter therefore must never trust a team_id pulled out of the request
// body without first reconciling it against identity.TeamId. The body team_id
// is useful for cross-checks, not for routing or authorization decisions.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AgentSwarm.Messaging.Slack.Identity;
using AgentSwarm.Messaging.Slack.Membership;
using AgentSwarm.Messaging.Slack.Security.Diagnostics;
using AgentSwarm.Messaging.Slack.Workspaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Slack.Security;

/// <summary>
/// MVC action filter that enforces per-workspace, per-channel, and per-user
/// authorization for Slack inbound traffic. Runs in the security pipeline as
/// the stage immediately following signature validation; emits a structured
/// authorization decision for every short-circuit so that the SOC dashboards
/// can correlate denials with upstream signature events.
/// </summary>
public sealed class SlackAuthorizationFilter : IAsyncActionFilter
{
    /// <summary>
    /// Reason codes for an authorization decision. These are surfaced to
    /// telemetry verbatim and are part of the security-pipeline contract;
    /// adding or renaming a member is a breaking change for downstream
    /// dashboards and the SOC playbooks. Keep this enum and the matching
    /// short-circuit branches in <see cref="OnActionExecutionAsync"/> in
    /// lockstep -- never silently bucket a new condition into an existing
    /// reason.
    /// </summary>
    public enum AuthorizationReason
    {
        Allowed = 0,

        /// <summary>The request did not carry a stamped signature identity.</summary>
        MissingIdentity = 1,

        /// <summary>No workspace exists for the signature-validated team id.</summary>
        UnknownWorkspace = 2,

        /// <summary>
        /// The workspace exists but is administratively disabled. This is a
        /// distinct outcome from <see cref="UnknownWorkspace"/> -- the SOC
        /// needs to be able to tell "workspace went away" apart from
        /// "operator pulled the kill switch" without inspecting the store.
        /// </summary>
        DisabledWorkspace = 3,

        /// <summary>
        /// The body-supplied team id does not match the team id attested by
        /// the signature. This is a defense-in-depth tripwire for
        /// payload-substitution attempts: a forged body cannot route us to
        /// a different workspace than the one whose signing secret signed
        /// the request.
        /// </summary>
        WorkspaceMismatch = 4,

        UnknownChannel = 5,
        ChannelNotMonitored = 6,
        UserNotAuthorized = 7,
        UserNotAMember = 8,
    }

    private const string SignatureIdentityItemKey = "slack.signature.identity";
    private const string BodyTeamIdItemKey = "slack.body.team_id";
    private const string BodyChannelIdItemKey = "slack.body.channel_id";
    private const string BodyUserIdItemKey = "slack.body.user_id";

    // -------------------------------------------------------------------------
    // Log-sanitisation constants
    // -------------------------------------------------------------------------
    //
    // The rejection log surfaces identifiers that -- directly (bodyUserId) or
    // transitively (channel.ChannelId is looked up under a body-controlled key
    // and could echo back operator-poisoned data) -- originate outside the
    // trust boundary. Downstream log sinks vary: file appenders preserve raw
    // bytes verbatim, Elasticsearch's default text mapping does not escape
    // control characters, and the SOC's grep-based playbooks parse lines on
    // CR/LF boundaries. Embedding `\r\n` (or a Unicode line-separator) into a
    // logged field is therefore enough to forge a synthetic log record below
    // the real one -- the classic CWE-117 log-injection. We defend by
    // collapsing any C0/C1 control character to '?' and truncating to a
    // generous-but-fixed length before the value reaches the logger. Slack
    // IDs are bounded by Slack itself, so the truncation cap is comfortably
    // above any legitimate value while still capping the blast radius of a
    // pathological forged payload.
    private const int MaxLogIdLength = 64;
    private const string LogPlaceholderWhenAbsent = "(none)";
    private const char LogControlReplacement = '?';
    private const string LogTruncationMarker = "...";

    private readonly ISlackWorkspaceStore _workspaceStore;
    private readonly IChannelMembershipResolver _membershipResolver;
    private readonly ISlackSecurityLog _securityLog;
    private readonly IAuthorizationDecisionSink _decisionSink;
    private readonly IOptionsMonitor<SlackAuthorizationOptions> _options;
    private readonly ILogger<SlackAuthorizationFilter> _logger;

    public SlackAuthorizationFilter(
        ISlackWorkspaceStore workspaceStore,
        IChannelMembershipResolver membershipResolver,
        ISlackSecurityLog securityLog,
        IAuthorizationDecisionSink decisionSink,
        IOptionsMonitor<SlackAuthorizationOptions> options,
        ILogger<SlackAuthorizationFilter> logger)
    {
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _membershipResolver = membershipResolver ?? throw new ArgumentNullException(nameof(membershipResolver));
        _securityLog = securityLog ?? throw new ArgumentNullException(nameof(securityLog));
        _decisionSink = decisionSink ?? throw new ArgumentNullException(nameof(decisionSink));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolution result produced by the per-stage resolve helpers below.
    /// Carries the resolved domain object on success and a reason code on
    /// failure. Keeping the failure reason on the resolution (rather than
    /// having helpers return null and forcing the caller to guess what
    /// went wrong) is what made the disabled-workspace conflation bug
    /// possible in the first place; this type fixes that structurally.
    /// </summary>
    private readonly record struct WorkspaceResolution(
        AuthorizationReason Reason,
        SlackWorkspace? Workspace,
        string? OffendingTeamId)
    {
        public bool IsAllowed => Reason == AuthorizationReason.Allowed;

        public static WorkspaceResolution Allowed(SlackWorkspace workspace)
            => new(AuthorizationReason.Allowed, workspace, null);

        public static WorkspaceResolution Unknown(string teamId)
            => new(AuthorizationReason.UnknownWorkspace, null, teamId);

        public static WorkspaceResolution Disabled(SlackWorkspace workspace)
            => new(AuthorizationReason.DisabledWorkspace, workspace, workspace.TeamId);

        public static WorkspaceResolution Mismatch(string stampedTeamId, string? bodyTeamId)
            => new(AuthorizationReason.WorkspaceMismatch, null, bodyTeamId ?? stampedTeamId);
    }

    private readonly record struct ChannelResolution(
        AuthorizationReason Reason,
        SlackChannel? Channel)
    {
        public bool IsAllowed => Reason == AuthorizationReason.Allowed;

        public static ChannelResolution Allowed(SlackChannel channel)
            => new(AuthorizationReason.Allowed, channel);

        public static ChannelResolution Unknown()
            => new(AuthorizationReason.UnknownChannel, null);

        public static ChannelResolution NotMonitored(SlackChannel channel)
            => new(AuthorizationReason.ChannelNotMonitored, channel);
    }

    private readonly record struct MembershipResolution(AuthorizationReason Reason)
    {
        public bool IsAllowed => Reason == AuthorizationReason.Allowed;

        public static MembershipResolution Allowed() => new(AuthorizationReason.Allowed);
        public static MembershipResolution NotAuthorized() => new(AuthorizationReason.UserNotAuthorized);
        public static MembershipResolution NotAMember() => new(AuthorizationReason.UserNotAMember);
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var cancellationToken = httpContext.RequestAborted;

        // ----- Stage 1: identity ------------------------------------------------
        // The signature middleware is required to stamp an identity. If we got
        // here without one the pipeline is misconfigured; deny rather than
        // assume the absent identity is benign.
        if (!httpContext.Items.TryGetValue(SignatureIdentityItemKey, out var identityObj)
            || identityObj is not SlackSignatureIdentity identity)
        {
            await ShortCircuitAsync(
                context,
                AuthorizationReason.MissingIdentity,
                workspace: null,
                channel: null,
                userId: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var bodyTeamId = httpContext.Items[BodyTeamIdItemKey] as string;
        var bodyChannelId = httpContext.Items[BodyChannelIdItemKey] as string;
        var bodyUserId = httpContext.Items[BodyUserIdItemKey] as string;

        // ----- Stage 2: workspace ----------------------------------------------
        var workspaceResolution =
            await ResolveWorkspaceAsync(identity, bodyTeamId, cancellationToken).ConfigureAwait(false);

        if (!workspaceResolution.IsAllowed)
        {
            await ShortCircuitAsync(
                context,
                workspaceResolution.Reason,
                workspaceResolution.Workspace,
                channel: null,
                userId: bodyUserId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var workspace = workspaceResolution.Workspace!;

        // ----- Stage 3: channel -------------------------------------------------
        var channelResolution =
            await ResolveChannelAsync(workspace, bodyChannelId, cancellationToken).ConfigureAwait(false);

        if (!channelResolution.IsAllowed)
        {
            await ShortCircuitAsync(
                context,
                channelResolution.Reason,
                workspace,
                channelResolution.Channel,
                userId: bodyUserId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var channel = channelResolution.Channel!;

        // ----- Stage 4: membership ---------------------------------------------
        var membershipResolution =
            await ResolveMembershipAsync(workspace, channel, bodyUserId, cancellationToken)
                .ConfigureAwait(false);

        if (!membershipResolution.IsAllowed)
        {
            await ShortCircuitAsync(
                context,
                membershipResolution.Reason,
                workspace,
                channel,
                userId: bodyUserId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Authorization succeeded -- attach the resolved entities for downstream
        // handlers so they don't re-query, and record an "Allowed" decision so
        // the SOC dashboards can compute approve-rates per workspace/channel.
        httpContext.Items["slack.authorization.workspace"] = workspace;
        httpContext.Items["slack.authorization.channel"] = channel;

        await _decisionSink.RecordAsync(
            new AuthorizationDecision(
                Reason: AuthorizationReason.Allowed,
                TeamId: workspace.TeamId,
                ChannelId: channel.ChannelId,
                UserId: bodyUserId,
                RequestId: identity.RequestId,
                TimestampUtc: DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        await next().ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Stage 2: workspace resolution
    // -------------------------------------------------------------------------
    //
    // The signature middleware has already verified the request body's HMAC
    // against the signing secret of exactly one workspace, and stamped that
    // workspace (along with its team id) onto the identity. Two failure modes
    // need to be handled here:
    //
    //   (a) The body's team_id disagrees with identity.TeamId. This is a
    //       payload-substitution attempt -- the body is claiming to come from
    //       a different workspace than the one whose secret signed it. We
    //       refuse outright and *do not* fall back to the store; touching the
    //       store on the body team_id would re-introduce the very bypass we
    //       are guarding against.
    //
    //   (b) The signature middleware ran in passive mode (non-event endpoint)
    //       and did not attach a stamped workspace. In that case we fall back
    //       to the store, but keyed strictly on identity.TeamId.
    //
    // Disabled-workspace handling is surfaced through its own reason code
    // (DisabledWorkspace) rather than collapsed to "not found"; conflating the
    // two would make the upcoming SOC dashboard for kill-switch events
    // impossible without re-querying the store.
    // -------------------------------------------------------------------------
    private async ValueTask<WorkspaceResolution> ResolveWorkspaceAsync(
        SlackSignatureIdentity identity,
        string? bodyTeamId,
        CancellationToken cancellationToken)
    {
        // (a) Body/signature divergence check. This MUST run before any store
        // fallback, otherwise a forged body team_id would silently key the
        // fallback lookup and could surface an entirely different workspace.
        if (!string.IsNullOrEmpty(bodyTeamId) &&
            !string.Equals(bodyTeamId, identity.TeamId, StringComparison.Ordinal))
        {
            _securityLog.PayloadTeamMismatch(
                stampedTeamId: identity.TeamId,
                bodyTeamId: bodyTeamId,
                requestId: identity.RequestId);

            return WorkspaceResolution.Mismatch(identity.TeamId, bodyTeamId);
        }

        var stamped = identity.Workspace;

        if (stamped is not null)
        {
            // Cache hit from the signature middleware. The stamped workspace
            // was looked up under the cryptographically-attested team id, so
            // we trust it -- but we still surface Enabled/Disabled explicitly.
            return stamped.Enabled
                ? WorkspaceResolution.Allowed(stamped)
                : WorkspaceResolution.Disabled(stamped);
        }

        // (b) No stamped workspace -- fall back to the store. Keyed on the
        // signature-validated team id; the body team id has at this point
        // either matched identity.TeamId or been rejected above, so it is
        // not used here.
        var resolved =
            await _workspaceStore.TryGetByTeamIdAsync(identity.TeamId, cancellationToken)
                .ConfigureAwait(false);

        if (resolved is null)
        {
            return WorkspaceResolution.Unknown(identity.TeamId);
        }

        return resolved.Enabled
            ? WorkspaceResolution.Allowed(resolved)
            : WorkspaceResolution.Disabled(resolved);
    }

    // -------------------------------------------------------------------------
    // Stage 3: channel resolution
    // -------------------------------------------------------------------------
    private async ValueTask<ChannelResolution> ResolveChannelAsync(
        SlackWorkspace workspace,
        string? bodyChannelId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(bodyChannelId))
        {
            return ChannelResolution.Unknown();
        }

        var channel =
            await _workspaceStore.TryGetChannelAsync(workspace.TeamId, bodyChannelId, cancellationToken)
                .ConfigureAwait(false);

        if (channel is null)
        {
            return ChannelResolution.Unknown();
        }

        if (!channel.Monitored)
        {
            return ChannelResolution.NotMonitored(channel);
        }

        return ChannelResolution.Allowed(channel);
    }

    // -------------------------------------------------------------------------
    // Stage 4: membership resolution
    // -------------------------------------------------------------------------
    private async ValueTask<MembershipResolution> ResolveMembershipAsync(
        SlackWorkspace workspace,
        SlackChannel channel,
        string? userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return MembershipResolution.NotAuthorized();
        }

        var options = _options.CurrentValue;

        if (options.RequireExplicitUserAllowList &&
            !channel.AllowedUserIds.Contains(userId, StringComparer.Ordinal))
        {
            return MembershipResolution.NotAuthorized();
        }

        var isMember =
            await _membershipResolver
                .IsMemberAsync(workspace.TeamId, channel.ChannelId, userId, cancellationToken)
                .ConfigureAwait(false);

        return isMember
            ? MembershipResolution.Allowed()
            : MembershipResolution.NotAMember();
    }

    // -------------------------------------------------------------------------
    // Short-circuit helper
    // -------------------------------------------------------------------------
    private async ValueTask ShortCircuitAsync(
        ActionExecutingContext context,
        AuthorizationReason reason,
        SlackWorkspace? workspace,
        SlackChannel? channel,
        string? userId,
        CancellationToken cancellationToken)
    {
        var identity = context.HttpContext.Items[SignatureIdentityItemKey] as SlackSignatureIdentity;
        var requestId = identity?.RequestId ?? context.HttpContext.TraceIdentifier;
        var teamId = workspace?.TeamId ?? identity?.TeamId;

        await _decisionSink.RecordAsync(
            new AuthorizationDecision(
                Reason: reason,
                TeamId: teamId,
                ChannelId: channel?.ChannelId,
                UserId: userId,
                RequestId: requestId,
                TimestampUtc: DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        // Sanitise every identifier before it reaches the logger. teamId comes
        // from the signature-validated identity or the store, and channelId is
        // a resolved record from the store -- but `userId` here is the raw
        // bodyUserId pulled off the inbound request, and the resolved channel
        // record was looked up under a body-controlled key, so an operator who
        // ingested a poisoned channel descriptor could still echo CR/LF into
        // the log. requestId may originate from an X-Slack-Request-Id header
        // (forwarded by the signature middleware) which is again outside the
        // trust boundary for log-forging purposes even when the HMAC over the
        // signed payload was valid. Treat them all as untrusted at the logging
        // boundary -- SanitizeForLog collapses control chars to '?' and caps
        // length, mirroring the JSON-escaping discipline used elsewhere in
        // the pipeline.
        _logger.LogWarning(
            "Slack request denied: reason={Reason} team={TeamId} channel={ChannelId} user={UserId} requestId={RequestId}",
            reason.ToString(),
            SanitizeForLog(teamId),
            SanitizeForLog(channel?.ChannelId),
            SanitizeForLog(userId),
            SanitizeForLog(requestId));

        // Reason code is surfaced on the response so the SOC can correlate
        // synthetic probes with their decisions. The HTTP status itself stays
        // intentionally coarse (200 OK with an empty body) to avoid handing
        // attackers a workspace-existence oracle -- the same status is
        // returned regardless of which stage denied the request.
        context.HttpContext.Response.Headers["X-Slack-Auth-Decision"] =
            reason.ToString("G", CultureInfo.InvariantCulture);

        context.Result = new OkResult();
    }

    /// <summary>
    /// Renders an identifier for inclusion in a structured log message in a
    /// shape that cannot be used to forge new log records.
    /// <para>
    /// Returns <see cref="LogPlaceholderWhenAbsent"/> for null/empty input.
    /// Otherwise truncates to <see cref="MaxLogIdLength"/> code units and
    /// replaces every Unicode control character (categories Cc/Cf, which
    /// include CR, LF, NEL, and the various line/paragraph separators that
    /// downstream log parsers split on) with
    /// <see cref="LogControlReplacement"/>. Truncated values get a trailing
    /// <see cref="LogTruncationMarker"/> so operators can see the value was
    /// capped rather than mistakenly believing it was the full identifier.
    /// </para>
    /// <para>
    /// This is intentionally not a general-purpose escape: we want a flat,
    /// human-grep-friendly rendering, not JSON or URI encoding. The values
    /// also flow through the structured-logging parameter slot, so the
    /// formatter still treats them as scalars; sanitisation just guarantees
    /// the eventual textual rendering is single-line and bounded.
    /// </para>
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return LogPlaceholderWhenAbsent;
        }

        var truncated = value.Length > MaxLogIdLength;
        var span = truncated ? value.AsSpan(0, MaxLogIdLength) : value.AsSpan();

        var buffer = new StringBuilder(span.Length + LogTruncationMarker.Length);
        foreach (var ch in span)
        {
            buffer.Append(IsLogUnsafe(ch) ? LogControlReplacement : ch);
        }

        if (truncated)
        {
            buffer.Append(LogTruncationMarker);
        }

        return buffer.ToString();
    }

    /// <summary>
    /// True for any character that can break log-line framing or smuggle a
    /// new record into the sink. This catches the obvious C0 control set
    /// (<c>\r</c>, <c>\n</c>, <c>\t</c>, NUL, etc.) via
    /// <see cref="char.IsControl(char)"/>, plus the Unicode line and
    /// paragraph separators U+2028/U+2029 which <c>IsControl</c> does not
    /// flag but which several JSON-line parsers (and a few JS-based log
    /// viewers) treat as record terminators.
    /// </summary>
    private static bool IsLogUnsafe(char ch)
        => char.IsControl(ch) || ch == '\u2028' || ch == '\u2029';
}
