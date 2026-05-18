// -----------------------------------------------------------------------
// <copyright file="SlackInboundAuthorizer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISlackInboundAuthorizer"/>. Mirrors the rules
/// of <see cref="SlackAuthorizationFilter"/> (workspace lookup,
/// channel allow-list, user-group membership) but evaluates them
/// against a queued <see cref="SlackInboundEnvelope"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The authorizer reuses the Stage 3.2 dependencies
/// (<see cref="ISlackWorkspaceConfigStore"/>,
/// <see cref="ISlackMembershipResolver"/>,
/// <see cref="ISlackAuthorizationAuditSink"/>,
/// <see cref="SlackAuthorizationOptions"/>) so the production
/// authorization surface stays a single source of truth across the
/// HTTP and background pipelines.
/// </para>
/// <para>
/// The authorizer ALWAYS writes a rejection audit record through the
/// configured sink on a rejection so the
/// <c>slack_audit_entry.outcome = rejected_auth</c> row is durably
/// persisted regardless of which pipeline (HTTP filter vs. async
/// ingestor) caught the request.
/// </para>
/// </remarks>
internal sealed class SlackInboundAuthorizer : ISlackInboundAuthorizer
{
    /// <summary>
    /// Synthetic <c>request_path</c> stamped onto the rejection
    /// audit record so triage queries can distinguish ingestor-side
    /// rejections from the HTTP filter's rejections (which carry the
    /// real Slack endpoint path). Mirrors the literal prefix the
    /// signature middleware uses for non-controller paths.
    /// </summary>
    public const string RequestPathPrefix = "ingestor://";

    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISlackMembershipResolver membershipResolver;
    private readonly ISlackAuthorizationAuditSink auditSink;
    private readonly IOptionsMonitor<SlackAuthorizationOptions> optionsMonitor;
    private readonly ISlackEphemeralResponder ephemeralResponder;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SlackInboundAuthorizer> logger;

    public SlackInboundAuthorizer(
        ISlackWorkspaceConfigStore workspaceStore,
        ISlackMembershipResolver membershipResolver,
        ISlackAuthorizationAuditSink auditSink,
        IOptionsMonitor<SlackAuthorizationOptions> optionsMonitor,
        ISlackEphemeralResponder ephemeralResponder,
        ILogger<SlackInboundAuthorizer> logger,
        TimeProvider? timeProvider = null)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.membershipResolver = membershipResolver ?? throw new ArgumentNullException(nameof(membershipResolver));
        this.auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        this.ephemeralResponder = ephemeralResponder ?? throw new ArgumentNullException(nameof(ephemeralResponder));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SlackInboundAuthorizationResult> AuthorizeAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        SlackAuthorizationOptions options = this.optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            // The HTTP filter exposes the same Enabled escape hatch
            // for non-production diagnostic flows. When disabled,
            // every envelope is treated as authorized without any
            // workspace lookup, matching the filter's behaviour.
            this.logger.LogDebug(
                "SlackInboundAuthorizer is disabled; allowing envelope idempotency_key={IdempotencyKey} source={SourceType} unconditionally.",
                envelope.IdempotencyKey,
                envelope.SourceType);
            return SlackInboundAuthorizationResult.Authorized(new SlackWorkspaceConfig
            {
                TeamId = envelope.TeamId,
                Enabled = true,
            });
        }

        if (string.IsNullOrWhiteSpace(envelope.TeamId))
        {
            return await this
                .RejectAsync(envelope, SlackAuthorizationRejectionReason.MissingTeamId,
                    "team_id is missing from the queued envelope.", ct)
                .ConfigureAwait(false);
        }

        SlackWorkspaceConfig? workspace = await this.workspaceStore
            .GetByTeamIdAsync(envelope.TeamId, ct)
            .ConfigureAwait(false);

        if (workspace is null || !workspace.Enabled)
        {
            return await this
                .RejectAsync(envelope, SlackAuthorizationRejectionReason.UnknownWorkspace,
                    FormattableString.Invariant($"team_id '{envelope.TeamId}' is not registered or is disabled."), ct)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(envelope.ChannelId)
            || !IsChannelAllowed(workspace, envelope.ChannelId!))
        {
            return await this
                .RejectAsync(envelope, SlackAuthorizationRejectionReason.DisallowedChannel,
                    FormattableString.Invariant(
                        $"channel '{envelope.ChannelId ?? "(none)"}' is not in AllowedChannelIds for team '{workspace.TeamId}'."), ct)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(envelope.UserId))
        {
            return await this
                .RejectAsync(envelope, SlackAuthorizationRejectionReason.UserNotInAllowedGroup,
                    "user_id is missing from the queued envelope.", ct)
                .ConfigureAwait(false);
        }

        bool authorized;
        try
        {
            authorized = await this.membershipResolver
                .IsUserInAnyAllowedGroupAsync(
                    workspace.TeamId,
                    envelope.UserId!,
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
            return await this
                .RejectAsync(envelope, SlackAuthorizationRejectionReason.MembershipResolutionFailed,
                    FormattableString.Invariant(
                        $"membership resolution failed for team '{ex.TeamId}' group '{ex.UserGroupId ?? "(unknown)"}': {ex.Message}"), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defense-in-depth: a custom ISlackMembershipResolver that
            // throws a raw exception should still produce a controlled
            // rejection rather than crashing the ingestor loop.
            this.logger.LogError(
                ex,
                "Unexpected exception of type {ExceptionType} while resolving Slack user-group membership for team {TeamId} user {UserId}.",
                ex.GetType().FullName,
                workspace.TeamId,
                envelope.UserId);
            return await this
                .RejectAsync(envelope, SlackAuthorizationRejectionReason.MembershipResolutionFailed,
                    "membership resolution failed unexpectedly.", ct)
                .ConfigureAwait(false);
        }

        if (!authorized)
        {
            return await this
                .RejectAsync(envelope, SlackAuthorizationRejectionReason.UserNotInAllowedGroup,
                    FormattableString.Invariant(
                        $"user '{envelope.UserId}' is not a member of any allowed user group in team '{workspace.TeamId}'."), ct)
                .ConfigureAwait(false);
        }

        return SlackInboundAuthorizationResult.Authorized(workspace);
    }

    private static bool IsChannelAllowed(SlackWorkspaceConfig workspace, string channelId)
    {
        string[] allowed = workspace.AllowedChannelIds ?? Array.Empty<string>();
        if (allowed.Length == 0)
        {
            // Mirrors SlackAuthorizationFilter: an empty allow-list is
            // deny-all. The workspace docstring is explicit about
            // this -- an unconfigured workspace is a misconfiguration
            // the operator must fix before any traffic is accepted.
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

    private async Task<SlackInboundAuthorizationResult> RejectAsync(
        SlackInboundEnvelope envelope,
        SlackAuthorizationRejectionReason reason,
        string errorDetail,
        CancellationToken ct)
    {
        // Iter-7 evaluator item #2 (story FR-008 "Audit": persist
        // Slack team ID, channel ID, thread timestamp, user ID,
        // command text, and response payload). The pipeline-side
        // authorizer previously passed CommandText: null, which made
        // the sink fall back to a synthetic
        // "authorization_rejected path=ingestor://command reason=..."
        // marker — losing the verbatim slash-command invocation an
        // operator needs to triage a denied-channel rejection. Reuse
        // the same SlackInboundEnvelopeAuditFields helper the success
        // / duplicate / error inbound audit rows use, so a rejected
        // command row carries the literal "/agent ask <args>" string
        // (matching the HTTP-facing SlackAuthorizationFilter path's
        // CommandText), block-action rejections carry the action_id,
        // and event rejections carry the event subtype. The helper
        // is tolerant of malformed payloads and falls back to null
        // when the envelope's RawPayload cannot be parsed — the sink
        // then uses its existing synthetic marker, preserving the
        // pre-fix behaviour for genuinely opaque envelopes.
        SlackInboundEnvelopeAuditFields auditFields = SlackInboundEnvelopeAuditFields.Extract(envelope);

        SlackAuthorizationAuditRecord record = new(
            ReceivedAt: this.timeProvider.GetUtcNow(),
            Reason: reason,
            Outcome: SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            RequestPath: RequestPathPrefix + DescribeSourceType(envelope.SourceType),
            TeamId: NullIfEmpty(envelope.TeamId),
            ChannelId: envelope.ChannelId,
            UserId: NullIfEmpty(envelope.UserId),
            CommandText: NullIfEmpty(auditFields.CommandText),
            ErrorDetail: errorDetail);

        try
        {
            await this.auditSink.WriteAsync(record, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to write Slack authorization rejection audit entry for ingestor envelope idempotency_key={IdempotencyKey}.",
                envelope.IdempotencyKey);
        }

        this.logger.LogWarning(
            "Slack ingestor authorization rejected: reason={Reason}, idempotency_key={IdempotencyKey}, team_id={TeamId}, channel_id={ChannelId}, user_id={UserId}, detail={Detail}.",
            reason,
            envelope.IdempotencyKey,
            envelope.TeamId,
            envelope.ChannelId,
            envelope.UserId,
            errorDetail);

        // Stage 8.2 AC-5: deliver the operator-configured rejection
        // text as an ephemeral reply to the originating user via the
        // envelope's captured response_url. This is the brief's
        // "ephemeral error message to the user" leg on the async
        // pipeline path -- the sync MVC SlackAuthorizationFilter
        // delivers the same text in the HTTP response body, but a
        // request that bypasses the sync filter (Socket Mode, or a
        // host that has detached the filter) reaches the originating
        // user only via response_url. The responder swallows
        // transport failures so a missed ephemeral cannot dead-letter
        // an otherwise-correct rejection (architecture.md §5.5
        // best-effort contract).
        await this.TryPostEphemeralRejectionAsync(envelope, ct).ConfigureAwait(false);

        return SlackInboundAuthorizationResult.Rejected(reason, errorDetail);
    }

    private async Task TryPostEphemeralRejectionAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(envelope.ResponseUrl))
        {
            // Events API callbacks and any payload Slack delivered
            // without response_url have no late-reply channel. The
            // HTTP-facing sync filter (SlackAuthorizationFilter)
            // remains the user-visible surface in that configuration;
            // the audit row above is the only durable footprint.
            return;
        }

        SlackAuthorizationOptions options = this.optionsMonitor.CurrentValue;
        string message = string.IsNullOrWhiteSpace(options.RejectionMessage)
            ? SlackAuthorizationOptions.DefaultRejectionMessage
            : options.RejectionMessage;

        try
        {
            await this.ephemeralResponder
                .SendEphemeralAsync(envelope.ResponseUrl, message, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ephemeral delivery is best-effort per the responder's
            // own contract; absorb any unexpected exception so the
            // pipeline dispatch loop is not poisoned by a Slack
            // transport hiccup.
            this.logger.LogWarning(
                ex,
                "Slack ingestor authorization rejection ephemeral post failed for idempotency_key={IdempotencyKey}; rejection row was still persisted.",
                envelope.IdempotencyKey);
        }
    }

    private static string DescribeSourceType(SlackInboundSourceType sourceType) => sourceType switch
    {
        SlackInboundSourceType.Event => "event",
        SlackInboundSourceType.Command => "command",
        SlackInboundSourceType.Interaction => "interaction",
        _ => "unspecified",
    };

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
