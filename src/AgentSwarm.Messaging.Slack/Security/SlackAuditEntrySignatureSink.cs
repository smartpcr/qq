// -----------------------------------------------------------------------
// <copyright file="SlackAuditEntrySignatureSink.cs" company="Microsoft Corp.">
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
/// <see cref="ISlackSignatureAuditSink"/> that maps every
/// <see cref="SlackSignatureAuditRecord"/> to the canonical
/// <see cref="SlackAuditEntry"/> shape and forwards it to
/// <see cref="ISlackAuditEntryWriter.AppendAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// requires that "rejected signatures are logged to audit". That maps
/// onto the <c>slack_audit_entry</c> row introduced by Stage 2.1 with
/// <see cref="SlackAuditEntry.Outcome"/> = <c>rejected_signature</c>.
/// This sink is the bridge between the validator's narrow
/// rejection-record type and the broad cross-direction audit table.
/// </para>
/// <para>
/// The sink derives <see cref="SlackAuditEntry.RequestType"/> from the
/// request path (<c>/api/slack/events</c> &#8594; <c>event</c>,
/// <c>/api/slack/commands</c> &#8594; <c>slash_command</c>,
/// <c>/api/slack/interactions</c> &#8594; <c>interaction</c>) so triage
/// queries can filter by audit shape without parsing free-form text.
/// </para>
/// </remarks>
public sealed class SlackAuditEntrySignatureSink : ISlackSignatureAuditSink
{
    /// <summary>
    /// Placeholder <see cref="SlackAuditEntry.TeamId"/> used when the
    /// request was rejected before <c>team_id</c> could be extracted
    /// from the body. The <see cref="SlackAuditEntryConfiguration"/>
    /// column is non-nullable; a stable token keeps queries by
    /// <c>team_id</c> deterministic.
    /// </summary>
    public const string UnknownTeamIdPlaceholder = "unknown";

    private readonly ISlackAuditEntryWriter writer;
    private readonly ILogger<SlackAuditEntrySignatureSink> logger;

    /// <summary>Creates a sink that forwards to <paramref name="writer"/>.</summary>
    public SlackAuditEntrySignatureSink(
        ISlackAuditEntryWriter writer,
        ILogger<SlackAuditEntrySignatureSink> logger)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task WriteAsync(SlackSignatureAuditRecord record, CancellationToken ct)
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
            // The validator has already decided to reject; the rejection
            // is logged regardless of audit-write outcome.
            this.logger.LogError(
                ex,
                "Failed to persist slack_audit_entry for signature rejection at path {Path} (team_id={TeamId}, reason={Reason}).",
                record.RequestPath,
                record.TeamId,
                record.Reason);
        }
    }

    /// <summary>
    /// Pure, deterministic mapping from
    /// <see cref="SlackSignatureAuditRecord"/> to
    /// <see cref="SlackAuditEntry"/>. Exposed as <see langword="public"/>
    /// so the Stage 3.1 tests can pin the field-by-field mapping
    /// independently of the writer wiring.
    /// </summary>
    public static SlackAuditEntry Map(SlackSignatureAuditRecord record)
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
            ChannelId = null,
            ThreadTs = null,
            MessageTs = null,
            UserId = null,
            CommandText = BuildCommandText(record),
            ResponsePayload = null,
            Outcome = SlackSignatureAuditRecord.RejectedSignatureOutcome,
            ErrorDetail = BuildErrorDetail(record),
            Timestamp = record.ReceivedAt,
        };
    }

    private static string DeriveRequestType(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath))
        {
            return "signature_rejection";
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

        return "signature_rejection";
    }

    private static string BuildCommandText(SlackSignatureAuditRecord record)
    {
        // The raw command/text is not parsed when signature validation
        // fails (the body might be from a hostile caller). Persist the
        // request path and rejection reason so triage queries can still
        // pivot on slack_audit_entry.command_text.
        return FormattableString.Invariant(
            $"signature_rejected path={record.RequestPath} reason={record.Reason}");
    }

    private static string BuildErrorDetail(SlackSignatureAuditRecord record)
    {
        string detail = string.IsNullOrEmpty(record.ErrorDetail) ? "(none)" : record.ErrorDetail!;
        return FormattableString.Invariant(
            $"reason={record.Reason}; detail={detail}; timestamp_header={record.TimestampHeader ?? "(none)"}");
    }

    private static string NewAuditId(DateTimeOffset receivedAt)
    {
        // architecture.md §3.5 (cross-referenced by SlackAuditEntry.Id's
        // XML doc) requires the audit entry id to be a ULID-shaped string
        // (26 chars, Crockford base32, lexicographically sortable). Using
        // a ULID keyed on the request's received-at timestamp also gives
        // triage queries a useful "newer ids sort after older ids" order
        // that a Guid.NewGuid().ToString("N") value would not provide.
        return Ulid.NewUlid(receivedAt);
    }
}
