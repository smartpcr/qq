// -----------------------------------------------------------------------
// <copyright file="SlackAuditEntryAuthorizationSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Identifiers;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="ISlackAuthorizationAuditSink"/> that maps every
/// <see cref="SlackAuthorizationAuditRecord"/> to the canonical
/// <see cref="SlackAuditEntry"/> shape and forwards it to
/// <see cref="ISlackAuditEntryWriter.AppendAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// requires that "rejected requests are logged with outcome = rejected_auth
/// including team_id, channel_id, and user_id". That maps onto the
/// <c>slack_audit_entry</c> row introduced by Stage 2.1 with
/// <see cref="SlackAuditEntry.Outcome"/> = <c>rejected_auth</c>. This
/// sink is the bridge between the filter's narrow rejection-record
/// type and the broad cross-direction audit table.
/// </para>
/// <para>
/// Mirrors the Stage 3.1 <see cref="SlackAuditEntrySignatureSink"/>
/// pattern -- the two security stages converge on identical
/// <see cref="SlackAuditEntry"/> shape so triage queries against the
/// audit table do not have to special-case which gate fired.
/// </para>
/// </remarks>
public sealed class SlackAuditEntryAuthorizationSink : ISlackAuthorizationAuditSink
{
    /// <summary>
    /// Placeholder <see cref="SlackAuditEntry.TeamId"/> used when the
    /// request was rejected before <c>team_id</c> could be extracted
    /// from the body. The <c>slack_audit_entry.team_id</c> column is
    /// non-nullable; a stable token keeps queries by <c>team_id</c>
    /// deterministic.
    /// </summary>
    public const string UnknownTeamIdPlaceholder = "unknown";

    private readonly ISlackAuditEntryWriter writer;
    private readonly ILogger<SlackAuditEntryAuthorizationSink> logger;

    /// <summary>Creates a sink that forwards to <paramref name="writer"/>.</summary>
    public SlackAuditEntryAuthorizationSink(
        ISlackAuditEntryWriter writer,
        ILogger<SlackAuditEntryAuthorizationSink> logger)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task WriteAsync(SlackAuthorizationAuditRecord record, CancellationToken ct)
    {
        if (record is null)
        {
            return;
        }

        SlackAuditEntry entry = Map(record);
        try
        {
            await this.writer.AppendAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Audit-pipeline failure must not break the request response.
            // The filter has already decided to reject; the rejection
            // is logged regardless of audit-write outcome.
            this.logger.LogError(
                ex,
                "Failed to persist slack_audit_entry for authorization rejection at path {Path} (team_id={TeamId}, channel_id={ChannelId}, user_id={UserId}, reason={Reason}).",
                record.RequestPath,
                record.TeamId,
                record.ChannelId,
                record.UserId,
                record.Reason);
        }
    }

    /// <summary>
    /// Pure, deterministic mapping from
    /// <see cref="SlackAuthorizationAuditRecord"/> to
    /// <see cref="SlackAuditEntry"/>. Exposed as <see langword="public"/>
    /// so Stage 3.2 tests can pin the field-by-field mapping
    /// independently of the writer wiring.
    /// </summary>
    public static SlackAuditEntry Map(SlackAuthorizationAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string id = NewAuditId(record.ReceivedAt);
        return new SlackAuditEntry
        {
            Id = id,
            CorrelationId = id,
            AgentId = null,
            TaskId = null,
            ConversationId = null,
            Direction = "inbound",
            RequestType = DeriveRequestType(record.RequestPath),
            TeamId = string.IsNullOrWhiteSpace(record.TeamId) ? UnknownTeamIdPlaceholder : record.TeamId!,
            ChannelId = record.ChannelId,
            ThreadTs = null,
            MessageTs = null,
            UserId = record.UserId,
            CommandText = BuildCommandText(record),
            ResponsePayload = null,
            Outcome = SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            ErrorDetail = BuildErrorDetail(record),
            Timestamp = record.ReceivedAt,
        };
    }

    private static string DeriveRequestType(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath))
        {
            return "authorization_rejection";
        }

        // Path is normalised in lower-case so the comparison is
        // case-insensitive without a per-call allocation.
        string path = requestPath.ToLowerInvariant();

        if (path.EndsWith("/events", StringComparison.Ordinal) || path.Contains("/events/", StringComparison.Ordinal))
        {
            return "event";
        }

        if (path.EndsWith("/commands", StringComparison.Ordinal) || path.Contains("/commands/", StringComparison.Ordinal))
        {
            return "slash_command";
        }

        if (path.EndsWith("/interactions", StringComparison.Ordinal) || path.Contains("/interactions/", StringComparison.Ordinal))
        {
            return "interaction";
        }

        return "authorization_rejection";
    }

    private static string BuildCommandText(SlackAuthorizationAuditRecord record)
    {
        // For slash commands the raw text is retained so triage queries
        // can pivot on slack_audit_entry.command_text. For non-command
        // surfaces we still persist a structured marker including the
        // request path and rejection reason so the row remains queryable.
        if (!string.IsNullOrEmpty(record.CommandText))
        {
            return record.CommandText!;
        }

        return FormattableString.Invariant(
            $"authorization_rejected path={record.RequestPath} reason={record.Reason}");
    }

    private static string BuildErrorDetail(SlackAuthorizationAuditRecord record)
    {
        string detail = string.IsNullOrEmpty(record.ErrorDetail) ? "(none)" : record.ErrorDetail!;
        return FormattableString.Invariant(
            $"reason={record.Reason}; detail={detail}; channel_id={record.ChannelId ?? "(none)"}; user_id={record.UserId ?? "(none)"}");
    }

    private static string NewAuditId(DateTimeOffset receivedAt)
    {
        // architecture.md §3.5 requires the audit entry id to be a
        // ULID-shaped string (26 chars, Crockford base32,
        // lexicographically sortable). Mirrors
        // SlackAuditEntrySignatureSink.NewAuditId so triage queries can
        // sort signature and authorization rejections together by id.
        return Ulid.NewUlid(receivedAt);
    }
}
