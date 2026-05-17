// -----------------------------------------------------------------------
// <copyright file="SlackModalAuditRecorder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Identifiers;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.Extensions.Logging;

/// <summary>
/// Emits <see cref="SlackAuditEntry"/> rows for every modal fast-path
/// invocation (success, duplicate, error). Pulled forward from Stage
/// 6.4 (architecture.md §5.3 step 5 / implementation-plan.md line 377)
/// so Stage 4.1's synchronous <c>views.open</c> call is observable in
/// the durable audit log -- not just in
/// <see cref="ILogger"/> output. Closes Stage 4.1 evaluator iter-2
/// item 3.
/// </summary>
/// <remarks>
/// <para>
/// Every record carries <c>direction = inbound</c> (because the human
/// initiated the action),
/// <c>request_type = modal_open</c> (matching the architecture's
/// enumeration in <see cref="SlackAuditEntry.RequestType"/>), and one of
/// <c>outcome = success | duplicate | error</c>. The
/// <c>response_payload</c> field captures the Slack error code on
/// failure so an operator can correlate the audit row with Slack's
/// API logs.
/// </para>
/// <para>
/// Writes are best-effort: any exception from the writer is logged
/// but never surfaced to the caller, because the human-facing
/// ephemeral response has already been built and we must not fail
/// the user-visible flow on an audit-pipeline blip.
/// </para>
/// </remarks>
internal sealed class SlackModalAuditRecorder
{
    /// <summary>Constant for
    /// <see cref="SlackAuditEntry.RequestType"/>.</summary>
    public const string RequestTypeModalOpen = "modal_open";

    /// <summary>Constant for
    /// <see cref="SlackAuditEntry.Direction"/>.</summary>
    public const string DirectionInbound = "inbound";

    /// <summary>Outcome marker for a successful views.open.</summary>
    public const string OutcomeSuccess = "success";

    /// <summary>Outcome marker for a duplicate fast-path invocation.</summary>
    public const string OutcomeDuplicate = "duplicate";

    /// <summary>Outcome marker for a failed views.open.</summary>
    public const string OutcomeError = "error";

    private readonly ISlackAuditEntryWriter writer;
    private readonly ILogger<SlackModalAuditRecorder> logger;
    private readonly TimeProvider timeProvider;

    public SlackModalAuditRecorder(
        ISlackAuditEntryWriter writer,
        ILogger<SlackModalAuditRecorder> logger,
        TimeProvider? timeProvider = null)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Records a successful modal_open invocation. The serialised
    /// Slack <c>view</c> payload that was sent to the
    /// <c>views.open</c> Web API call is captured in
    /// <see cref="SlackAuditEntry.ResponsePayload"/> when supplied,
    /// fulfilling the story "Audit" requirement that the response
    /// payload be persisted alongside the team / channel / thread /
    /// user / command fields.
    /// </summary>
    /// <param name="envelope">Inbound envelope that triggered the modal.</param>
    /// <param name="subCommand">Lower-case sub-command name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="responsePayload">
    /// Serialised Slack <c>view</c> JSON (optionally bounded to a
    /// reasonable size by the caller via
    /// <see cref="SlackAuditPayloadSerializer.Serialize"/>). May be
    /// <see langword="null"/> if the caller has no access to the
    /// payload (legacy call sites).
    /// </param>
    public Task RecordSuccessAsync(
        SlackInboundEnvelope envelope,
        string subCommand,
        CancellationToken ct,
        string? responsePayload = null)
        => this.AppendAsync(envelope, subCommand, OutcomeSuccess, errorDetail: null, responsePayload, ct);

    /// <summary>
    /// Records a duplicate fast-path invocation (idempotency guard
    /// rejected the second attempt).
    /// </summary>
    public Task RecordDuplicateAsync(
        SlackInboundEnvelope envelope,
        string subCommand,
        string diagnostic,
        CancellationToken ct,
        string? responsePayload = null)
        => this.AppendAsync(envelope, subCommand, OutcomeDuplicate, diagnostic, responsePayload, ct);

    /// <summary>
    /// Records a failed modal_open invocation (Slack error, network
    /// failure, missing configuration, etc.).
    /// </summary>
    public Task RecordErrorAsync(
        SlackInboundEnvelope envelope,
        string subCommand,
        string errorDetail,
        CancellationToken ct)
        => this.AppendAsync(envelope, subCommand, OutcomeError, errorDetail, responsePayload: null, ct);

    private async Task AppendAsync(
        SlackInboundEnvelope envelope,
        string subCommand,
        string outcome,
        string? errorDetail,
        string? responsePayload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        string id = Ulid.NewUlid(now);

        // Stage 7.1 evaluator iter-1 item 3: on success the
        // ResponsePayload must carry the serialised Slack view JSON
        // (story "Audit" field list). On error, errorDetail wins so
        // the audit row remains diagnosable. On duplicate, prefer
        // the payload when supplied, otherwise fall back to the
        // diagnostic so the row still records "why".
        string? payloadForRow = string.Equals(outcome, OutcomeError, StringComparison.Ordinal)
            ? errorDetail
            : (responsePayload ?? errorDetail);

        SlackAuditEntry entry = new()
        {
            Id = id,
            CorrelationId = envelope.IdempotencyKey,
            AgentId = null,
            TaskId = null,
            ConversationId = null,
            Direction = DirectionInbound,
            RequestType = RequestTypeModalOpen,
            TeamId = string.IsNullOrEmpty(envelope.TeamId) ? "unknown" : envelope.TeamId,
            ChannelId = envelope.ChannelId,
            ThreadTs = null,
            MessageTs = null,
            UserId = string.IsNullOrEmpty(envelope.UserId) ? null : envelope.UserId,
            CommandText = $"/agent {subCommand}",
            ResponsePayload = payloadForRow,
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
            // Best-effort: the user already has their ephemeral
            // response. Logging is enough; the audit pipeline's own
            // retries (when one ships) will recover.
            this.logger.LogError(
                ex,
                "Failed to append modal_open audit entry id={Id} outcome={Outcome} team={TeamId} user={UserId}.",
                id,
                outcome,
                envelope.TeamId,
                envelope.UserId);
        }
    }
}
