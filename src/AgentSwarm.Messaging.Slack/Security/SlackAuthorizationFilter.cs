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
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
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
    public const string WorkspaceItemKey = SlackSignatureValidator.WorkspaceItemKey;

    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which the resolved
    /// Slack <c>channel_id</c> is stamped on success.
    /// </summary>
    public const string ChannelIdItemKey = "AgentSwarm.Slack.Authorization.ChannelId";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which the resolved
    /// Slack <c>user_id</c> is stamped on success.
    /// </summary>
    public const string UserIdItemKey = "AgentSwarm.Slack.Authorization.UserId";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key set to <see langword="true"/>
    /// after the filter has successfully authorized the request. Lets a
    /// downstream filter or controller short-circuit when the request
    /// has already been classified, without re-running the ACL.
    /// </summary>
    public const string AuthorizedItemKey = "AgentSwarm.Slack.Authorization.Authorized";

    private const string EphemeralResponseContentType = "application/json; charset=utf-8";

    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISlackMembershipResolver membershipResolver;
    private readonly ISlackAuthorizationAuditSink auditSink;
    private readonly IOptionsMonitor<SlackAuthorizationOptions> optionsMonitor;
    private readonly IOptionsMonitor<SlackSignatureOptions> signatureOptionsMonitor;
    private readonly ILogger<SlackAuthorizationFilter> logger;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a new instance with the supplied dependencies.
    /// </summary>
    /// <remarks>
    /// <paramref name="signatureOptionsMonitor"/> is the single source of
    /// truth for the URL path scope: the filter reads
    /// <see cref="SlackSignatureOptions.PathPrefix"/> on every request
    /// so the authorization gate and the upstream HMAC middleware are
    /// mathematically guaranteed to enforce on the same surface area.
    /// There is no <c>SlackAuthorizationOptions.PathPrefix</c> by design.
    /// </remarks>
    public SlackAuthorizationFilter(
        ISlackWorkspaceConfigStore workspaceStore,
        ISlackMembershipResolver membershipResolver,
        ISlackAuthorizationAuditSink auditSink,
        IOptionsMonitor<SlackAuthorizationOptions> optionsMonitor,
        IOptionsMonitor<SlackSignatureOptions> signatureOptionsMonitor,
        ILogger<SlackAuthorizationFilter> logger,
        TimeProvider? timeProvider = null)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.membershipResolver = membershipResolver ?? throw new ArgumentNullException(nameof(membershipResolver));
        this.auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        this.signatureOptionsMonitor = signatureOptionsMonitor ?? throw new ArgumentNullException(nameof(signatureOptionsMonitor));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
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

        HttpContext http = context.HttpContext;

        // Path scoping (evaluator iter-2 follow-up): the filter is
        // mounted as a global MVC filter so future Slack controllers
        // inherit the gate automatically. The path scope is read from
        // SlackSignatureOptions.PathPrefix -- the SAME option the
        // upstream HMAC middleware uses -- so the two layers cannot
        // drift apart. An operator who moves Slack:Signature:PathPrefix
        // from /api/slack to /slack-gateway moves BOTH the signature
        // gate and the authorization gate; there is no separate
        // Slack:Authorization:PathPrefix to forget. Non-Slack MVC
        // endpoints (admin APIs, cache-invalidation hooks, future
        // controllers unrelated to Slack) short-circuit out of the
        // ACL without parsing the body or looking up the workspace.
        SlackSignatureOptions signatureOptions = this.signatureOptionsMonitor.CurrentValue;
        if (!PathInScope(http, signatureOptions))
        {
            await next().ConfigureAwait(false);
            return;
        }

        CancellationToken ct = http.RequestAborted;

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

        if (string.IsNullOrWhiteSpace(identity.ChannelId)
            || !IsChannelAllowed(workspace, identity.ChannelId!))
        {
            // Stage 4.1 iter-2 evaluator item 1: view_submission
            // (modal form submission) payloads are NOT channel-scoped
            // by Slack's design -- the modal lives in
            // private_metadata, not channel.id. The Stage 3.2 ACL
            // still enforces workspace + user-group membership, but
            // the channel allow-list cannot apply when the payload
            // has no channel context to compare against. Skip the
            // channel rejection for view_submission so authorized
            // modal submissions can reach
            // /api/slack/interactions and be turned into
            // HumanDecisionEvent in Stage 5.3. Block-action
            // interactions and slash commands still carry a
            // channel.id and remain subject to the allow-list.
            if (IsViewSubmissionPayload(identity))
            {
                this.logger.LogDebug(
                    "Slack authorization filter skipping channel ACL for view_submission payload team={TeamId} user={UserId} (modal submissions are not channel-scoped per architecture.md §5.3).",
                    identity.TeamId,
                    identity.UserId);
            }
            else
            {
                await this
                    .RejectAsync(context, identity, SlackAuthorizationRejectionReason.DisallowedChannel,
                        FormattableString.Invariant(
                            $"channel '{identity.ChannelId ?? "(none)"}' is not in AllowedChannelIds for team '{workspace.TeamId}'."))
                    .ConfigureAwait(false);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(identity.UserId))
        {
            await this
                .RejectAsync(context, identity, SlackAuthorizationRejectionReason.UserNotInAllowedGroup,
                    "user_id is missing from the request body.")
                .ConfigureAwait(false);
            return;
        }

        bool authorized;
        try
        {
            authorized = await this.membershipResolver
                .IsUserInAnyAllowedGroupAsync(
                    workspace.TeamId,
                    identity.UserId!,
                    workspace.AllowedUserGroupIds ?? Array.Empty<string>(),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SlackMembershipResolutionException ex)
        {
            await this
                .RejectAsync(context, identity, SlackAuthorizationRejectionReason.MembershipResolutionFailed,
                    FormattableString.Invariant(
                        $"membership resolution failed for team '{ex.TeamId}' group '{ex.UserGroupId ?? "(unknown)"}': {ex.Message}"))
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            // Defense-in-depth: SlackMembershipResolver wraps its own
            // failures, but a custom ISlackMembershipResolver might
            // not. Fail closed.
            this.logger.LogError(
                ex,
                "Unexpected exception of type {ExceptionType} while resolving Slack user-group membership for team {TeamId} user {UserId}.",
                ex.GetType().FullName,
                workspace.TeamId,
                identity.UserId);
            await this
                .RejectAsync(context, identity, SlackAuthorizationRejectionReason.MembershipResolutionFailed,
                    "membership resolution failed unexpectedly.")
                .ConfigureAwait(false);
            return;
        }

        if (!authorized)
        {
            await this
                .RejectAsync(context, identity, SlackAuthorizationRejectionReason.UserNotInAllowedGroup,
                    FormattableString.Invariant(
                        $"user '{identity.UserId}' is not a member of any allowed user group in team '{workspace.TeamId}'."))
                .ConfigureAwait(false);
            return;
        }

        // Happy path: stamp resolved identity so downstream stages do
        // not re-parse the body.
        http.Items[WorkspaceItemKey] = workspace;
        if (!string.IsNullOrEmpty(identity.ChannelId))
        {
            // view_submission payloads have no channel.id by design;
            // skip stamping the ChannelIdItemKey so a downstream
            // consumer that probes Items[ChannelIdItemKey] sees the
            // absent value rather than an empty string masquerading
            // as a resolved channel.
            http.Items[ChannelIdItemKey] = identity.ChannelId!;
        }

        http.Items[UserIdItemKey] = identity.UserId!;
        http.Items[AuthorizedItemKey] = true;

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// True when the inbound identity was extracted from a
    /// Slack <c>view_submission</c> (modal form submission) payload.
    /// Slack's view_submission interactive payload is not
    /// channel-scoped -- the modal's origin is conveyed via
    /// <c>view.private_metadata</c>, not <c>channel.id</c> -- so the
    /// authorization filter must skip the channel ACL on this
    /// surface while still enforcing workspace + user-group ACL.
    /// </summary>
    /// <remarks>
    /// Stage 4.1 iter-2 evaluator item 1. The
    /// <see cref="SlackInboundIdentityExtractor"/> populates
    /// <see cref="SlackInboundIdentity.PayloadType"/> from the
    /// top-level Slack <c>type</c> discriminator on JSON payloads
    /// (Events API callbacks and interactive payloads), which is
    /// where Slack surfaces <c>view_submission</c> per the
    /// Block Kit + modal API docs.
    /// </remarks>
    private static bool IsViewSubmissionPayload(SlackInboundIdentity identity)
    {
        return string.Equals(
            identity.PayloadType,
            "view_submission",
            StringComparison.Ordinal);
    }

    private static bool IsChannelAllowed(SlackWorkspaceConfig workspace, string channelId)
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

        return false;
    }

    private static bool PathInScope(HttpContext http, SlackSignatureOptions signatureOptions)
    {
        // SlackSignatureOptions.PathPrefix is the single source of truth
        // for the URL scope covered by both the HMAC middleware and
        // this authorization filter. SlackSignatureValidationServiceCollectionExtensions
        // enforces a non-empty, leading-'/' value at startup, so any
        // value reaching this point is already a valid PathString.
        if (string.IsNullOrWhiteSpace(signatureOptions.PathPrefix))
        {
            // Defense-in-depth: a custom composition root that bypasses
            // the validator (e.g., a test) and leaves PathPrefix empty
            // means "no scope filter" -- enforce on every action.
            return true;
        }

        return http.Request.Path.StartsWithSegments(
            new PathString(signatureOptions.PathPrefix),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SlackWorkspaceConfig?> ResolveWorkspaceAsync(HttpContext http, string teamId, CancellationToken ct)
    {
        // Reuse the workspace stamped by the signature middleware when
        // it ran for the same team. This avoids a redundant store
        // lookup on the hot path.
        if (http.Items.TryGetValue(WorkspaceItemKey, out object? cached)
            && cached is SlackWorkspaceConfig stamped
            && string.Equals(stamped.TeamId, teamId, StringComparison.Ordinal))
        {
            return stamped.Enabled ? stamped : null;
        }

        return await this.workspaceStore.GetByTeamIdAsync(teamId, ct).ConfigureAwait(false);
    }

    private async Task RejectAsync(
        ActionExecutingContext context,
        SlackInboundIdentity identity,
        SlackAuthorizationRejectionReason reason,
        string errorDetail)
    {
        HttpContext http = context.HttpContext;
        SlackAuthorizationOptions options = this.optionsMonitor.CurrentValue;

        SlackAuthorizationAuditRecord record = new(
            ReceivedAt: this.timeProvider.GetUtcNow(),
            Reason: reason,
            Outcome: SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            RequestPath: http.Request.Path.Value ?? string.Empty,
            TeamId: identity.TeamId,
            ChannelId: identity.ChannelId,
            UserId: identity.UserId,
            CommandText: identity.CommandText,
            ErrorDetail: errorDetail);

        try
        {
            await this.auditSink
                .WriteAsync(record, http.RequestAborted)
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
        string message = string.IsNullOrWhiteSpace(options.RejectionMessage)
            ? SlackAuthorizationOptions.DefaultRejectionMessage
            : options.RejectionMessage;

        // Slack endpoints MUST return HTTP 200 (architecture.md ┬º2.4
        // and the Stage 3.2 brief). The brief explicitly requires
        // rejection to be communicated in the response body as an
        // ephemeral message for every Slack inbound surface
        // (commands, interactions, AND Events API callbacks). Even
        // though Slack's Events API does not render
        // {response_type:"ephemeral",text:...} to end users, emitting
        // the same shape on every path gives operators, audit
        // consumers, and Stage 4.x retry logic a uniform body to
        // parse and keeps the rejection contract honest with the
        // brief.
        string body = BuildEphemeralBody(message);
        return new ContentResult
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

        // Sanitise every identifier before it lands in the structured logger.
        //
        // On the rejection path some of these values originate in the request
        // body (notably userId, and in WorkspaceMismatch / pre-resolution
        // paths the body team_id / channel id too), so a forged payload can
        // smuggle CR/LF or other control characters. Text sinks (rolling
        // file, journald, plain Elasticsearch via Serilog text formatter)
        // would happily render those bytes verbatim, allowing an attacker
        // to inject fake log lines or break field framing. SanitizeIdForLog
        // strips anything outside the Slack object-id alphabet and truncates
        // overlong values so the log shape is always predictable.
        //
        // teamId and channelId are usually sourced from trusted stamps/store
        // lookups, but we sanitise unconditionally so this stays correct if
        // somebody later wires raw body values into the failure path.
        _logger.LogWarning(
            "Slack request denied: reason={Reason} team={TeamId} channel={ChannelId} user={UserId} requestId={RequestId}",
            reason.ToString(),
            SanitizeIdForLog(teamId),
            SanitizeIdForLog(channel?.ChannelId),
            SanitizeIdForLog(userId),
            SanitizeIdForLog(requestId));

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
    /// Sanitises an identifier value pulled from the inbound request body (or
    /// derived from upstream identity stamps) before it appears as a
    /// structured log parameter on the rejection path.
    ///
    /// The rejection path is by definition reached with attacker-controlled
    /// input: a forged body can put arbitrary bytes into team_id /
    /// channel_id / user_id, including CR / LF that would forge new log
    /// lines on text sinks (rolling file, journald) or break field framing
    /// on structured sinks that flatten to JSON-lines (Elasticsearch via the
    /// default Serilog text formatter, Splunk universal forwarder, ...).
    ///
    /// We restrict the output to the Slack object-id alphabet
    /// (<c>[A-Za-z0-9_.-]</c>), replacing every other byte with <c>?</c>,
    /// and truncate at <see cref="LogIdMaxLength"/>. When truncation
    /// occurs, a trailing <c>...(truncated)</c> marker is appended so SOC
    /// analysts can tell the value was clipped rather than naturally short.
    /// Null and empty values render as <c>(none)</c>, preserving the
    /// behaviour the log message contract previously had with the
    /// <c>?? "(none)"</c> coalesce.
    /// </summary>
    private static string SanitizeIdForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(none)";
        }

        var truncated = value.Length > LogIdMaxLength;
        var span = truncated ? value.AsSpan(0, LogIdMaxLength) : value.AsSpan();

        var buffer = new StringBuilder(span.Length + (truncated ? 14 : 0));

        foreach (var ch in span)
        {
            var safe = ch is (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_' or '-' or '.';

            buffer.Append(safe ? ch : '?');
        }

        if (truncated)
        {
            buffer.Append("...(truncated)");
        }

        return buffer.ToString();
    }
}
