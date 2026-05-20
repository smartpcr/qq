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
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SlackInboundAuthorizer> logger;
    private readonly ISlackEphemeralResponder? ephemeralResponder;

    public SlackInboundAuthorizer(
        ISlackWorkspaceConfigStore workspaceStore,
        ISlackMembershipResolver membershipResolver,
        ISlackAuthorizationAuditSink auditSink,
        IOptionsMonitor<SlackAuthorizationOptions> optionsMonitor,
        ILogger<SlackInboundAuthorizer> logger,
        TimeProvider? timeProvider = null)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.membershipResolver = membershipResolver ?? throw new ArgumentNullException(nameof(membershipResolver));
        this.auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.ephemeralResponder = null;
    }

    /// <summary>
    /// Stage 4.2 iter-2: DI-preferred constructor that also accepts
    /// the <see cref="ISlackEphemeralResponder"/> registered by the
    /// command/interaction dispatch wiring. When a rejected envelope
    /// carries a <c>response_url</c> (always true for slash commands
    /// over both HTTP and Socket Mode -- see
    /// <see cref="Transport.SlackInboundEnvelopeFactory"/> and
    /// <see cref="Transport.SlackSocketModePayloadNormalizer"/>) the
    /// authorizer POSTs the configured rejection wording so the
    /// originating user sees an ephemeral error. This is the ONLY
    /// user-visible reply channel for Socket Mode rejections because
    /// the WebSocket ACK frame only carries the envelope_id with no
    /// body slot for a user-facing message (architecture.md §4.2
    /// + Stage 8.2 AC-5).
    /// </summary>
    public SlackInboundAuthorizer(
        ISlackWorkspaceConfigStore workspaceStore,
        ISlackMembershipResolver membershipResolver,
        ISlackAuthorizationAuditSink auditSink,
        IOptionsMonitor<SlackAuthorizationOptions> optionsMonitor,
        ILogger<SlackInboundAuthorizer> logger,
        TimeProvider timeProvider,
        ISlackEphemeralResponder ephemeralResponder)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.membershipResolver = membershipResolver ?? throw new ArgumentNullException(nameof(membershipResolver));
        this.auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.ephemeralResponder = ephemeralResponder ?? throw new ArgumentNullException(nameof(ephemeralResponder));
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
        // Stage 4.2 iter-2: extract the verbatim command_text via the
        // shared audit-field helper so the rejected_auth row carries
        // the literal slash command (e.g. "/agent ask <prompt>") an
        // operator needs to triage the rejection. Critically, the
        // helper auto-detects JSON-shaped Socket Mode payloads vs
        // form-encoded HTTP payloads, so a denied-channel Socket
        // Mode rejection no longer leaks the synthetic
        // "authorization_rejected path=..." marker into command_text
        // (story Audit FR-008 requires the verbatim command).
        SlackInboundEnvelopeAuditFields auditFields = SlackInboundEnvelopeAuditFields.Extract(envelope);

        SlackAuthorizationAuditRecord record = new(
            ReceivedAt: this.timeProvider.GetUtcNow(),
            Reason: reason,
            Outcome: SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            RequestPath: RequestPathPrefix + DescribeSourceType(envelope.SourceType),
            TeamId: NullIfEmpty(envelope.TeamId),
            ChannelId: envelope.ChannelId,
            UserId: NullIfEmpty(envelope.UserId),
            CommandText: auditFields.CommandText,
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

        // Stage 8.2 AC-5: deliver the user-facing ephemeral via
        // response_url. This is best-effort; ISlackEphemeralResponder
        // swallows transient HTTP errors so a delivery failure does
        // not crash the pipeline -- the audit row is already durable.
        await this.TryPostEphemeralRejectionAsync(envelope, ct).ConfigureAwait(false);

        return SlackInboundAuthorizationResult.Rejected(reason, errorDetail);
    }

    /// <summary>
    /// POSTs the operator-configured rejection wording to the
    /// envelope's <see cref="SlackInboundEnvelope.ResponseUrl"/> when
    /// one is present. No-op when the responder was not wired (unit
    /// tests using the 6-arg ctor), when the envelope lacks a
    /// response_url (Events API callbacks, Socket Mode events), or
    /// when the configured wording is whitespace.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="SlackAuthorizationFilter"/>'s fallback to
    /// <see cref="SlackAuthorizationOptions.DefaultRejectionMessage"/>
    /// so the HTTP and async surfaces deliver the same user-visible
    /// text regardless of which one caught the rejection.
    /// </remarks>
    private async Task TryPostEphemeralRejectionAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        if (this.ephemeralResponder is null)
        {
            return;
        }

        string? responseUrl = envelope.ResponseUrl;
        if (string.IsNullOrWhiteSpace(responseUrl))
        {
            return;
        }

        SlackAuthorizationOptions options = this.optionsMonitor.CurrentValue;
        string message = string.IsNullOrWhiteSpace(options.RejectionMessage)
            ? SlackAuthorizationOptions.DefaultRejectionMessage
            : options.RejectionMessage;

        try
        {
            await this.ephemeralResponder
                .SendEphemeralAsync(responseUrl, message, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: the contract on ISlackEphemeralResponder is
            // that implementations swallow transient HTTP errors. A
            // throwing implementation MUST NOT escalate into a DLQ
            // because the rejection itself is the terminal disposition.
            this.logger.LogWarning(
                ex,
                "Slack ingestor authorization: failed to POST ephemeral rejection to response_url for idempotency_key={IdempotencyKey}; audit row was persisted.",
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
