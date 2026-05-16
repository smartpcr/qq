// -----------------------------------------------------------------------
// <copyright file="ISlackAuthorizationAuditSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Sink that receives a <see cref="SlackAuthorizationAuditRecord"/> every
/// time <see cref="SlackAuthorizationFilter"/> rejects an inbound action
/// invocation. Production wiring persists the record into the
/// <c>slack_audit_entry</c> table introduced by Stage 2.2; tests and
/// developer setups can register the no-op or in-memory implementations
/// shipped in this namespace.
/// </summary>
/// <remarks>
/// The filter does not write audit entries for successful requests here
/// -- those are recorded by downstream pipeline stages (ingestion,
/// dispatch, completion) once the envelope reaches the handler. Keeping
/// the sink scoped to rejections honours the implementation-plan brief:
/// <em>"log audit entry with outcome = rejected_auth including team_id,
/// channel_id, and user_id"</em>.
/// </remarks>
public interface ISlackAuthorizationAuditSink
{
    /// <summary>
    /// Persists the supplied rejection record. Implementations should
    /// swallow non-fatal failures (e.g., transient database errors)
    /// after logging them, because the filter has already decided to
    /// reject the request and must not allow audit failures to leak
    /// state to the caller.
    /// </summary>
    Task WriteAsync(SlackAuthorizationAuditRecord record, CancellationToken ct);
}

/// <summary>
/// Snapshot of a single rejection observed by
/// <see cref="SlackAuthorizationFilter"/>.
/// </summary>
/// <param name="ReceivedAt">UTC timestamp at which the request was rejected.</param>
/// <param name="Reason">Discriminator describing why the request was rejected.</param>
/// <param name="Outcome">
/// Outcome string written to <c>slack_audit_entry.outcome</c>. Always
/// <see cref="SlackAuthorizationAuditRecord.RejectedAuthOutcome"/> for
/// rejections raised by the authorization filter.
/// </param>
/// <param name="RequestPath">
/// HTTP request path (e.g., <c>/api/slack/commands</c>) the request was
/// targeting. Retained for triage.
/// </param>
/// <param name="TeamId">
/// Slack <c>team_id</c> extracted from the request body when available;
/// <see langword="null"/> when the body could not be parsed.
/// </param>
/// <param name="ChannelId">
/// Slack <c>channel_id</c> extracted from the request body when
/// available; <see langword="null"/> when the payload is not channel-
/// scoped or could not be parsed.
/// </param>
/// <param name="UserId">
/// Slack <c>user_id</c> of the human who triggered the request;
/// <see langword="null"/> when the payload is not user-scoped or could
/// not be parsed.
/// </param>
/// <param name="CommandText">
/// Raw slash-command text (e.g., <c>ask generate a plan</c>) when the
/// request is a slash command; <see langword="null"/> for events and
/// interactions where command text is not present.
/// </param>
/// <param name="ErrorDetail">
/// Optional free-form detail (for example
/// <c>"channel C99 not in AllowedChannelIds for team T0123ABCD"</c>).
/// The value MUST NOT include any resolved secret material.
/// </param>
public sealed record SlackAuthorizationAuditRecord(
    DateTimeOffset ReceivedAt,
    SlackAuthorizationRejectionReason Reason,
    string Outcome,
    string RequestPath,
    string? TeamId,
    string? ChannelId,
    string? UserId,
    string? CommandText,
    string? ErrorDetail)
{
    /// <summary>
    /// Canonical <c>slack_audit_entry.outcome</c> value for rejections
    /// raised by the authorization filter. Mirrors the literal in
    /// architecture.md §3.5 and the implementation-plan Stage 3.2
    /// acceptance scenarios.
    /// </summary>
    public const string RejectedAuthOutcome = "rejected_auth";
}
