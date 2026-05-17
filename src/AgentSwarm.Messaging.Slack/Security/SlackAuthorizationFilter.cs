// -----------------------------------------------------------------------
// <copyright file="SlackAuthorizationFilter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// ASP.NET Core <see cref="IAsyncActionFilter"/> implementing Stage 3.2
/// of <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// -- the three-layer Slack ACL (workspace, channel, user-group
/// membership). Runs AFTER Stage 3.1's
/// <see cref="SlackSignatureValidator"/> middleware so HMAC verification
/// has already passed before the filter inspects the payload.
/// </summary>
/// <remarks>
/// <para>
/// The filter enforces the layers in order:
/// </para>
/// <list type="number">
///   <item><description>Workspace: <c>team_id</c> resolves to a
///   <see cref="SlackWorkspaceConfig"/> with
///   <see cref="SlackWorkspaceConfig.Enabled"/> = <see langword="true"/>.</description></item>
///   <item><description>Channel: <c>channel_id</c> is non-empty and is
///   listed in <see cref="SlackWorkspaceConfig.AllowedChannelIds"/>.</description></item>
///   <item><description>User-group membership: the requester's
///   <c>user_id</c> belongs to at least one user group in
///   <see cref="SlackWorkspaceConfig.AllowedUserGroupIds"/>
///   (resolved through <see cref="ISlackMembershipResolver"/>).</description></item>
/// </list>
/// <para>
/// Rejection contract per the brief: Slack endpoints MUST return HTTP 200
/// (Slack treats other status codes as transport failures and retries).
/// The filter therefore short-circuits the action with a
/// <see cref="ContentResult"/> carrying an ephemeral message JSON body
/// (<c>{"response_type":"ephemeral","text":"..."}</c>) on EVERY Slack
/// inbound surface -- slash commands, interactions, AND Events API
/// callbacks. Even though Slack does not render ephemeral text for
/// Events API responses, emitting the same uniform body on every path
/// keeps the rejection contract honest with the brief and gives
/// audit consumers / Stage 4.x retry tooling a single shape to parse.
/// Every rejection is forwarded to <see cref="ISlackAuthorizationAuditSink"/>
/// with <see cref="SlackAuthorizationAuditRecord.RejectedAuthOutcome"/>
/// so the audit pipeline can persist it to <c>slack_audit_entry</c>.
/// </para>
/// <para>
/// On success the filter stamps the resolved workspace, channel id, and
/// user id back onto <see cref="HttpContext.Items"/> so downstream
/// stages (idempotency guard, command handler) do not have to re-parse
/// the body.
/// </para>
/// </remarks>
public sealed class SlackAuthorizationFilter : IAsyncActionFilter
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which the resolved
    /// <see cref="SlackWorkspaceConfig"/> is stamped on success. Reuses
    /// the same key Stage 3.1's signature middleware writes to so that
    /// when the filter reads the workspace from
    /// <see cref="HttpContext.Items"/> instead of re-querying the
    /// store, both producers and consumers agree on the contract.
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

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        SlackAuthorizationOptions options = this.optionsMonitor.CurrentValue;

        if (!options.Enabled)
        {
            await next().ConfigureAwait(false);
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

        // Events API url_verification handshakes have no channel /
        // user; the signature validator already accepted them and the
        // /api/slack/events endpoint will respond with the challenge.
        // Skip the ACL entirely so the handshake is never rejected.
        if (http.Items.TryGetValue(SlackSignatureValidator.UrlVerificationItemKey, out object? urlVerify)
            && urlVerify is true)
        {
            await next().ConfigureAwait(false);
            return;
        }

        SlackInboundIdentity identity;
        try
        {
            identity = await SlackInboundIdentityExtractor
                .ExtractAsync(http)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to parse Slack inbound payload for authorization at path {Path}.",
                http.Request.Path.Value);
            identity = SlackInboundIdentity.Empty;
        }

        if (string.IsNullOrWhiteSpace(identity.TeamId))
        {
            await this
                .RejectAsync(context, identity, SlackAuthorizationRejectionReason.MissingTeamId,
                    "team_id is missing from the request body.")
                .ConfigureAwait(false);
            return;
        }

        SlackWorkspaceConfig? workspace = await this
            .ResolveWorkspaceAsync(http, identity.TeamId!, ct)
            .ConfigureAwait(false);

        if (workspace is null || !workspace.Enabled)
        {
            await this
                .RejectAsync(context, identity, SlackAuthorizationRejectionReason.UnknownWorkspace,
                    FormattableString.Invariant($"team_id '{identity.TeamId}' is not registered or is disabled."))
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(identity.ChannelId)
            || !IsChannelAllowed(workspace, identity.ChannelId!))
        {
            await this
                .RejectAsync(context, identity, SlackAuthorizationRejectionReason.DisallowedChannel,
                    FormattableString.Invariant(
                        $"channel '{identity.ChannelId ?? "(none)"}' is not in AllowedChannelIds for team '{workspace.TeamId}'."))
                .ConfigureAwait(false);
            return;
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
        http.Items[ChannelIdItemKey] = identity.ChannelId!;
        http.Items[UserIdItemKey] = identity.UserId!;
        http.Items[AuthorizedItemKey] = true;

        await next().ConfigureAwait(false);
    }

    private static bool IsChannelAllowed(SlackWorkspaceConfig workspace, string channelId)
    {
        string[] allowed = workspace.AllowedChannelIds ?? Array.Empty<string>();
        if (allowed.Length == 0)
        {
            // Empty allow-list = deny-all per SlackWorkspaceConfig
            // docstring. The architecture treats an unconfigured
            // workspace as a misconfiguration the operator must fix
            // before any inbound traffic is accepted.
            return false;
        }

        for (int i = 0; i < allowed.Length; i++)
        {
            if (string.Equals(allowed[i], channelId, StringComparison.Ordinal))
            {
                return true;
            }
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
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to write Slack authorization rejection audit entry for path {Path}.",
                http.Request.Path.Value);
        }

        this.logger.LogWarning(
            "Slack authorization rejected: reason={Reason}, path={Path}, team_id={TeamId}, channel_id={ChannelId}, user_id={UserId}, detail={Detail}.",
            reason,
            http.Request.Path.Value,
            identity.TeamId,
            identity.ChannelId,
            identity.UserId,
            errorDetail);

        context.Result = BuildRejectionResult(http, options);
    }

    private static ActionResult BuildRejectionResult(HttpContext http, SlackAuthorizationOptions options)
    {
        string message = string.IsNullOrWhiteSpace(options.RejectionMessage)
            ? SlackAuthorizationOptions.DefaultRejectionMessage
            : options.RejectionMessage;

        // Slack endpoints MUST return HTTP 200 (architecture.md §2.4
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
            StatusCode = (int)HttpStatusCode.OK,
            ContentType = EphemeralResponseContentType,
            Content = body,
        };
    }

    private static string BuildEphemeralBody(string message)
    {
        // Hand-rolled JSON keeps the response body free of serializer
        // surprises (no surrogate framework dependency, no extra
        // escaping ambiguity). The only string we interpolate is the
        // operator-supplied rejection message, which we escape for
        // JSON safety.
        string escaped = JsonEscape(message);
        return "{\"response_type\":\"ephemeral\",\"text\":\"" + escaped + "\"}";
    }

    private static string JsonEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        System.Text.StringBuilder buffer = new(value.Length + 8);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\\':
                    buffer.Append("\\\\");
                    break;
                case '"':
                    buffer.Append("\\\"");
                    break;
                case '\n':
                    buffer.Append("\\n");
                    break;
                case '\r':
                    buffer.Append("\\r");
                    break;
                case '\t':
                    buffer.Append("\\t");
                    break;
                case '\b':
                    buffer.Append("\\b");
                    break;
                case '\f':
                    buffer.Append("\\f");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        buffer.Append("\\u");
                        buffer.Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        buffer.Append(ch);
                    }

                    break;
            }
        }

        return buffer.ToString();
    }
}
