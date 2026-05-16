// -----------------------------------------------------------------------
// <copyright file="ISlackSignatureAuditSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Sink that receives a <see cref="SlackSignatureAuditRecord"/> every
/// time <see cref="SlackSignatureValidator"/> rejects an inbound HTTP
/// request. Production wiring persists the record into the
/// <c>slack_audit_entry</c> table introduced by Stage 2.2; tests and
/// developer setups can register the no-op or in-memory implementations
/// shipped in this namespace.
/// </summary>
/// <remarks>
/// The validator does not write audit entries for successful requests
/// here -- those are recorded by downstream pipeline stages (ingestion,
/// dispatch, completion) once the envelope reaches the handler. Keeping
/// the sink scoped to rejections honours the implementation-plan brief:
/// <em>"Reject ... by returning HTTP 401 and logging an audit entry
/// with outcome = rejected_signature"</em>.
/// </remarks>
public interface ISlackSignatureAuditSink
{
    /// <summary>
    /// Persists the supplied rejection record. Implementations should
    /// swallow non-fatal failures (e.g., transient database errors)
    /// after logging them, because the validator has already decided to
    /// reject the request and must not allow audit failures to leak
    /// state to the caller.
    /// </summary>
    Task WriteAsync(SlackSignatureAuditRecord record, CancellationToken ct);
}

/// <summary>
/// Snapshot of a single rejection observed by
/// <see cref="SlackSignatureValidator"/>.
/// </summary>
/// <param name="ReceivedAt">UTC timestamp at which the request was rejected.</param>
/// <param name="Reason">Discriminator describing why the request was rejected.</param>
/// <param name="Outcome">
/// Outcome string written to <c>slack_audit_entry.outcome</c>. Always
/// <see cref="SlackSignatureAuditRecord.RejectedSignatureOutcome"/> for
/// rejections raised by the signature validator.
/// </param>
/// <param name="RequestPath">
/// HTTP request path (e.g., <c>/api/slack/events</c>) the request was
/// targeting. Retained for triage.
/// </param>
/// <param name="TeamId">
/// Slack <c>team_id</c> extracted from the request body when available;
/// <see langword="null"/> when the body could not be parsed or the
/// header set was missing before parsing began.
/// </param>
/// <param name="SignatureHeader">
/// Raw <c>X-Slack-Signature</c> header value, truncated by the caller
/// when needed. Useful for operator triage; never includes the resolved
/// signing secret.
/// </param>
/// <param name="TimestampHeader">
/// Raw <c>X-Slack-Request-Timestamp</c> header value. Stored for replay
/// triage.
/// </param>
/// <param name="ErrorDetail">
/// Optional free-form detail (for example "timestamp 1714410000 is
/// 1,200 seconds older than now"). The value MUST NOT include the
/// signing secret.
/// </param>
public sealed record SlackSignatureAuditRecord(
    DateTimeOffset ReceivedAt,
    SlackSignatureRejectionReason Reason,
    string Outcome,
    string RequestPath,
    string? TeamId,
    string? SignatureHeader,
    string? TimestampHeader,
    string? ErrorDetail)
{
    /// <summary>
    /// Canonical <c>slack_audit_entry.outcome</c> value for rejections
    /// raised by signature validation. Mirrors the literal in
    /// architecture.md §3.5 and the implementation-plan acceptance
    /// scenarios.
    /// </summary>
    public const string RejectedSignatureOutcome = "rejected_signature";
}
