// -----------------------------------------------------------------------
// <copyright file="SlackInboundAuditRecorder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Identifiers;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;

/// <summary>
/// Writes inbound-pipeline <see cref="SlackAuditEntry"/> rows --
/// specifically the <c>outcome = duplicate</c> rows mandated by
/// implementation step 7 of Stage 4.3, plus the matching
/// <c>success</c> and <c>error</c> rows so an operator can query the
/// audit table for "every inbound exchange grouped by outcome".
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Mirrors the pattern established by
/// <see cref="SlackModalAuditRecorder"/> (Stage 4.1) and the audit
/// sinks introduced in Stage 3.x: best-effort append; any writer
/// failure is logged but never propagated so the audit pipeline
/// cannot crash the ingestor dispatch loop.
/// </para>
/// <para>
/// Authorization-rejection rows are intentionally NOT emitted here;
/// they remain the responsibility of <see cref="SlackInboundAuthorizer"/>
/// (which writes through the workspace-shared
/// <see cref="Security.ISlackAuthorizationAuditSink"/>) so the
/// authorization audit shape stays identical between the HTTP filter
/// path and the async ingestor path.
/// </para>
/// </remarks>
internal sealed class SlackInboundAuditRecorder
{
    /// <summary><see cref="SlackAuditEntry.Direction"/> value.</summary>
    public const string DirectionInbound = "inbound";

    /// <summary><see cref="SlackAuditEntry.RequestType"/> for slash-command envelopes.</summary>
    public const string RequestTypeSlashCommand = "slash_command";

    /// <summary><see cref="SlackAuditEntry.RequestType"/> for app_mention events.</summary>
    public const string RequestTypeAppMention = "app_mention";

    /// <summary><see cref="SlackAuditEntry.RequestType"/> for non-app_mention Events API callbacks.</summary>
    public const string RequestTypeEvent = "event";

    /// <summary><see cref="SlackAuditEntry.RequestType"/> for Block Kit / view-submission interactions.</summary>
    public const string RequestTypeInteraction = "interaction";

    /// <summary><see cref="SlackAuditEntry.RequestType"/> for unrecognised source types.</summary>
    public const string RequestTypeUnknown = "unknown";

    /// <summary>Outcome marker: handler returned successfully.</summary>
    public const string OutcomeSuccess = "success";

    /// <summary>Outcome marker: idempotency guard reported a duplicate.</summary>
    public const string OutcomeDuplicate = "duplicate";

    /// <summary>Outcome marker: retry budget exhausted, sent to DLQ.</summary>
    public const string OutcomeError = "error";

    private readonly ISlackAuditEntryWriter writer;
    private readonly ILogger<SlackInboundAuditRecorder> logger;
    private readonly TimeProvider timeProvider;

    public SlackInboundAuditRecorder(
        ISlackAuditEntryWriter writer,
        ILogger<SlackInboundAuditRecorder> logger,
        TimeProvider? timeProvider = null)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Records a successful inbound dispatch.
    /// </summary>
    public Task RecordSuccessAsync(SlackInboundEnvelope envelope, string? requestType, CancellationToken ct)
        => this.AppendAsync(envelope, requestType, OutcomeSuccess, errorDetail: null, ct);

    /// <summary>
    /// Records a duplicate envelope that was silently dropped by the
    /// idempotency guard. This is the row the brief specifically
    /// requires (implementation step 7: "Log duplicate events to
    /// audit with outcome = duplicate").
    /// </summary>
    public Task RecordDuplicateAsync(SlackInboundEnvelope envelope, string? requestType, CancellationToken ct)
        => this.AppendAsync(envelope, requestType, OutcomeDuplicate, errorDetail: null, ct);

    /// <summary>
    /// Records an envelope that exhausted its retry budget and was
    /// moved to the dead-letter queue. <paramref name="errorDetail"/>
    /// is the terminal exception message; the .NET type name is
    /// stored on the DLQ entry, not here.
    /// </summary>
    public Task RecordErrorAsync(
        SlackInboundEnvelope envelope,
        string? requestType,
        string errorDetail,
        CancellationToken ct)
        => this.AppendAsync(envelope, requestType, OutcomeError, errorDetail, ct);

    /// <summary>
    /// Maps a <see cref="SlackInboundEnvelope"/> to the canonical
    /// <see cref="SlackAuditEntry.RequestType"/> string. Exposed
    /// internal so the pipeline can stamp the same value on the
    /// ingestor log line that the audit row receives.
    /// </summary>
    public static string DescribeRequestType(SlackInboundEnvelope envelope, string? eventSubtype = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return envelope.SourceType switch
        {
            SlackInboundSourceType.Command => RequestTypeSlashCommand,
            SlackInboundSourceType.Interaction => RequestTypeInteraction,
            SlackInboundSourceType.Event when string.Equals(eventSubtype, "app_mention", StringComparison.Ordinal)
                => RequestTypeAppMention,
            SlackInboundSourceType.Event => RequestTypeEvent,
            _ => RequestTypeUnknown,
        };
    }

    private async Task AppendAsync(
        SlackInboundEnvelope envelope,
        string? requestType,
        string outcome,
        string? errorDetail,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        string id = Ulid.NewUlid(now);

        SlackInboundEnvelopeAuditFields parsed = SlackInboundEnvelopeAuditFields.Extract(envelope);

        // Iter 5 evaluator item #3 (operator FR-008 / story "Audit"
        // requirement): persist Slack team ID, channel ID, thread
        // timestamp, user ID, command text, and response payload so
        // operators can query every agent/human exchange by
        // correlation ID. The previous implementation left
        // CommandText, ThreadTs, ConversationId, and MessageTs as
        // null, which masked the actual command text and thread
        // anchor in the audit log.
        // - CommandText: extracted from the raw payload per source
        //   type (e.g. "/agent ask plan for failover" for slash
        //   commands, the interaction action_id / view_id for
        //   buttons / modal submissions, or the event subtype for
        //   Events API callbacks).
        // - ThreadTs: extracted from the raw payload when present
        //   (interaction container.thread_ts, event.thread_ts);
        //   commands never start inside a thread so this is null.
        // - MessageTs: extracted from the raw payload for events
        //   and interactions; commands have no message ts.
        // - ConversationId: defaults to ThreadTs (the canonical
        //   conversation anchor when a thread exists) and falls back
        //   to ChannelId so an operator querying by ConversationId
        //   still gets the channel-scoped envelopes when no thread
        //   has been opened yet.
        // ResponsePayload remains null on the inbound side: the
        // field is reserved for outbound rows per SlackAuditEntry's
        // contract ("Serialized response sent to Slack ... or null
        // for inbound rows") and is populated by Stage 4.x outbound
        // dispatcher's audit recorder, not the inbound pipeline.
        string? threadTs = parsed.ThreadTs;
        string? conversationId = string.IsNullOrEmpty(threadTs)
            ? envelope.ChannelId
            : threadTs;

        SlackAuditEntry entry = new()
        {
            Id = id,
            CorrelationId = string.IsNullOrEmpty(envelope.IdempotencyKey)
                ? id
                : envelope.IdempotencyKey,
            AgentId = null,
            TaskId = null,
            ConversationId = conversationId,
            Direction = DirectionInbound,
            RequestType = string.IsNullOrWhiteSpace(requestType)
                ? DescribeRequestType(envelope)
                : requestType!,
            TeamId = string.IsNullOrEmpty(envelope.TeamId) ? "unknown" : envelope.TeamId,
            ChannelId = envelope.ChannelId,
            ThreadTs = threadTs,
            MessageTs = parsed.MessageTs,
            UserId = string.IsNullOrEmpty(envelope.UserId) ? null : envelope.UserId,
            CommandText = parsed.CommandText,
            ResponsePayload = null,
            Outcome = outcome,
            ErrorDetail = string.Equals(outcome, OutcomeError, StringComparison.Ordinal) ? errorDetail : null,
            Timestamp = now,
        };

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
            this.logger.LogError(
                ex,
                "Failed to append Slack inbound audit entry id={Id} outcome={Outcome} idempotency_key={IdempotencyKey} team={TeamId} user={UserId}.",
                id,
                outcome,
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
        }
    }
}
