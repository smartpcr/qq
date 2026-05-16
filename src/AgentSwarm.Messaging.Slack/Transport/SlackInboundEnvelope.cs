namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Normalized inbound payload produced by the Slack transport layer
/// (<c>SlackEventsApiReceiver</c>, <c>SlackSlashCommandReceiver</c>,
/// <c>SlackInteractionsReceiver</c>, or <c>SlackSocketModeReceiver</c>)
/// after signature verification. The envelope is the unit of work buffered
/// by <see cref="Queues.ISlackInboundQueue"/> and drained by the
/// <c>SlackInboundIngestor</c> background service.
/// </summary>
/// <remarks>
/// COMPILE STUB introduced by Stage 1.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. The
/// canonical field surface is owned by Stage 3.1 (Slack Inbound Transport)
/// line 193 of <c>implementation-plan.md</c>, which spells out exactly
/// these fields:
/// <c>IdempotencyKey, SourceType, TeamId, ChannelId, UserId, RawPayload,
/// TriggerId (nullable), ReceivedAt</c>. Defining the record here lets the
/// queue contracts compile without forcing Stage 3.1 to revise the field
/// list.
/// </remarks>
/// <param name="IdempotencyKey">
/// Slack-derived deduplication key. Sourced from <c>event_id</c> for Events
/// API callbacks and <c>trigger_id</c> for slash commands / interactions
/// (see architecture.md section 3.4). Used by <c>SlackIdempotencyGuard</c>
/// to suppress duplicates from Slack's at-least-once redelivery.
/// </param>
/// <param name="SourceType">Discriminator selecting the downstream handler.</param>
/// <param name="TeamId">Slack workspace identifier (<c>team_id</c>).</param>
/// <param name="ChannelId">
/// Slack channel identifier. Nullable because some Events API callbacks
/// (e.g., workspace-level events) are not channel-scoped.
/// </param>
/// <param name="UserId">Slack user identifier of the human who triggered the payload.</param>
/// <param name="RawPayload">
/// Verbatim JSON (or form-encoded) payload as received from Slack. Retained
/// so that downstream handlers can decode SlackNet-typed views and so that
/// <c>SlackAuditLogger</c> can persist a hash for audit replay.
/// </param>
/// <param name="TriggerId">
/// Slack <c>trigger_id</c> (short-lived, modal-opening token). Present only
/// on commands and interactions; <c>null</c> for Events API callbacks.
/// </param>
/// <param name="ReceivedAt">UTC timestamp at which the transport layer accepted the request.</param>
internal sealed record SlackInboundEnvelope(
    string IdempotencyKey,
    SlackInboundSourceType SourceType,
    string TeamId,
    string? ChannelId,
    string UserId,
    string RawPayload,
    string? TriggerId,
    DateTimeOffset ReceivedAt);
