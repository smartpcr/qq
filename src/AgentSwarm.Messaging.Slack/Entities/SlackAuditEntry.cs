namespace AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Immutable audit log entry for every Slack exchange (inbound or outbound).
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. Field
/// list is the canonical surface specified by architecture.md section 3.5.
/// </para>
/// <para>
/// "Immutable" is enforced by the audit pipeline (rows are inserted but
/// never updated). The properties themselves expose <c>{ get; set; }</c>
/// so EF Core can hydrate the entity on read.
/// </para>
/// <para>
/// <see cref="Id"/> is expected to be a ULID-shaped string (Crockford
/// base32, 26 characters, lexicographically sortable). Generation and
/// validation of the ULID is performed by the audit-write path; this
/// entity does NOT enforce the format.
/// </para>
/// </remarks>
public sealed class SlackAuditEntry
{
    /// <summary>
    /// Unique entry identifier. Expected to be a ULID-shaped string;
    /// the format is not enforced at the entity level. Primary key.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// End-to-end correlation identifier propagated through every
    /// message in the exchange (FR-004).
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the agent involved in the exchange. <c>null</c> for
    /// inbound human-initiated requests where no agent has yet been
    /// assigned.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Identifier of the task involved in the exchange. <c>null</c> when
    /// the request has not yet been mapped to a task.
    /// </summary>
    public string? TaskId { get; set; }

    /// <summary>
    /// Logical conversation identifier (typically the Slack thread).
    /// <c>null</c> for top-level posts that do not belong to a thread.
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// <c>inbound</c> or <c>outbound</c>.
    /// </summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>
    /// Request shape: <c>slash_command</c>, <c>event</c>, <c>app_mention</c>,
    /// <c>interaction</c>, <c>message_send</c>, <c>modal_open</c>, or
    /// <c>message_update</c>.
    /// </summary>
    public string RequestType { get; set; } = string.Empty;

    /// <summary>
    /// Slack workspace identifier.
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// Slack channel identifier, or <c>null</c> when the exchange is not
    /// channel-scoped.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Slack thread timestamp the exchange belongs to, or <c>null</c>
    /// for a top-level post.
    /// </summary>
    public string? ThreadTs { get; set; }

    /// <summary>
    /// Slack message timestamp produced or observed by this exchange,
    /// or <c>null</c> if no Slack message was directly involved.
    /// </summary>
    public string? MessageTs { get; set; }

    /// <summary>
    /// Slack user identifier, or <c>null</c> for outbound posts not
    /// authored by an end user.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Raw command or action text observed on the inbound side. Used for
    /// audit replay and triage. <c>null</c> for outbound rows.
    /// </summary>
    public string? CommandText { get; set; }

    /// <summary>
    /// Serialized response sent to Slack (typically Block Kit JSON), or
    /// <c>null</c> for inbound rows.
    /// </summary>
    public string? ResponsePayload { get; set; }

    /// <summary>
    /// Outcome marker: <c>success</c>, <c>rejected_auth</c>,
    /// <c>rejected_signature</c>, <c>duplicate</c>, or <c>error</c>.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Error message when <see cref="Outcome"/> is <c>error</c>,
    /// otherwise <c>null</c>.
    /// </summary>
    public string? ErrorDetail { get; set; }

    /// <summary>
    /// UTC timestamp at which the exchange occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }
}
