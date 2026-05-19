namespace AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Persisted ledger of inbound Slack requests, used by
/// <c>SlackIdempotencyGuard</c> to suppress duplicate processing across
/// Slack's at-least-once redelivery semantics. One row per unique
/// <see cref="IdempotencyKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. Field
/// list is the canonical surface specified by architecture.md section 3.3.
/// Idempotency key derivation rules are documented in section 3.4 (the key
/// is computed by the transport layer; this record only stores the result).
/// </para>
/// </remarks>
public sealed class SlackInboundRequestRecord
{
    /// <summary>
    /// Derived per-surface deduplication key (see architecture.md
    /// section 3.4). Primary key. Examples:
    /// <list type="bullet">
    ///   <item><description><c>event:{event_id}</c> for Events API callbacks</description></item>
    ///   <item><description><c>cmd:{team}:{user}:{cmd}:{trigger_id}</c> for slash commands</description></item>
    ///   <item><description><c>interact:{team}:{user}:{action_or_view}:{trigger_id}</c> for interactions</description></item>
    /// </list>
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Discriminator: <c>event</c>, <c>command</c>, or <c>interaction</c>.
    /// Stored as a string for portability across stores that do not
    /// natively support .NET enums.
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Slack workspace identifier that originated the request.
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// Slack channel identifier, or <c>null</c> for workspace-level events
    /// that are not channel-scoped.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Slack user identifier of the human who triggered the request.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the raw payload body, retained so an operator can
    /// confirm a stored record matches a replayed Slack envelope.
    /// </summary>
    public string RawPayloadHash { get; set; } = string.Empty;

    /// <summary>
    /// Processing lifecycle state: <c>received</c>, <c>processing</c>,
    /// <c>completed</c>, or <c>failed</c>. Stored as a string for the
    /// same portability reason as <see cref="SourceType"/>.
    /// </summary>
    public string ProcessingStatus { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp at which the dedup store first <b>persisted</b>
    /// this idempotency key -- i.e. the moment the
    /// <c>SlackIdempotencyGuard.TryAcquireAsync</c> insert succeeded
    /// (or the reclaim CAS bumped the row). Doubles as the lease-
    /// acquired-at marker that
    /// <c>SlackIdempotencyOptions.StaleProcessingThresholdSeconds</c>
    /// measures against. <b>NOT</b> the transport-layer
    /// <c>envelope.ReceivedAt</c>: an envelope that sat in the
    /// inbound queue longer than the stale threshold must NOT be
    /// inserted with a pre-stale lease, or a concurrent Slack retry
    /// could reclaim it while the original handler is still running
    /// (the iter-2 evaluator regression). The envelope's transport-
    /// received timestamp is audited separately by
    /// <c>SlackInboundAuditRecorder</c>.
    /// </summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>
    /// UTC timestamp at which processing reached a terminal state
    /// (<c>completed</c> or <c>failed</c>). <c>null</c> while the
    /// request is still in flight.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
