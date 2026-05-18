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
    DateTimeOffset ReceivedAt)
{
    /// <summary>
    /// Slack-issued <c>response_url</c> captured from the inbound
    /// payload. Present on slash-command and interactive payloads only;
    /// <see langword="null"/> for Events API callbacks (which have no
    /// response_url). Carried on the envelope so any handler dispatched
    /// from the BackgroundService pipeline -- including the Stage 4.3
    /// <see cref="Pipeline.SlackInboundAuthorizer"/> on a rejection --
    /// can deliver an ephemeral reply to the originating user via
    /// <see cref="Pipeline.ISlackEphemeralResponder"/> after the HTTP
    /// transport has already ACK'd Slack with HTTP 200.
    /// </summary>
    /// <remarks>
    /// Stage 8.2 AC-5: the brief's "rejected during async processing
    /// with an ephemeral error message to the user" leg requires the
    /// pipeline-side authorizer to reach the original requester
    /// AFTER the controller has completed the HTTP response. The only
    /// Slack-supported channel for that late reply is response_url
    /// (valid ~30 min for up to five posts), so the envelope carries
    /// it through the queue to the dispatch loop. Property is
    /// non-positional (<c>init</c>-only) so this addition does not
    /// disturb the eight positional record parameters or any existing
    /// constructor call sites.
    /// </remarks>
    public string? ResponseUrl { get; init; }
}
