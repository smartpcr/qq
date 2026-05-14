using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Append-only audit record covering inbound commands, outbound notifications, and Adaptive
/// Card action callbacks. Implements the canonical schema in <c>tech-spec.md</c> §4.3 with
/// the addition of an implementation-specific <see cref="Checksum"/> field (SHA-256 over the
/// canonical fields) used for tamper detection at the storage layer.
/// </summary>
/// <remarks>
/// <para>
/// Every property uses an <c>init</c> setter so an instance is fully immutable after
/// construction — callers cannot mutate an entry via <c>with</c> expressions and downstream
/// audit storage can rely on the canonical-row invariant. The <c>required</c> modifier
/// guarantees the construction site populates the canonical fields; optional fields default
/// to <see langword="null"/>.
/// </para>
/// <para>
/// The init setters for <see cref="EventType"/>, <see cref="ActorType"/>, and
/// <see cref="Outcome"/> reject any value outside the canonical vocabulary defined in
/// <c>tech-spec.md</c> §4.3 (and mirrored in <see cref="AuditEventTypes.All"/>,
/// <see cref="AuditActorTypes.All"/>, <see cref="AuditOutcomes.All"/>). Validation runs at
/// both construction time and on each <c>with</c> expression — callers cannot create or
/// derive an <see cref="AuditEntry"/> with an invalid discriminator.
/// </para>
/// <para>
/// To populate <see cref="Checksum"/>, call <see cref="ComputeChecksum"/> with the same
/// canonical-field values used to construct the entry. The checksum is computed over a
/// length-prefixed binary representation of every canonical field so two implementations
/// agree on the digest given identical canonical inputs and no choice of payload content
/// (including embedded delimiters) can collide with a different field decomposition.
/// </para>
/// </remarks>
public sealed record AuditEntry
{
    private readonly string _eventType = string.Empty;
    private readonly string _actorType = string.Empty;
    private readonly string _outcome = string.Empty;

    /// <summary>UTC time the event occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>End-to-end trace ID for distributed tracing.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Audit category — one of <see cref="AuditEventTypes.All"/>:
    /// <c>CommandReceived</c>, <c>MessageSent</c>, <c>CardActionReceived</c>,
    /// <c>SecurityRejection</c>, <c>ProactiveNotification</c>,
    /// <c>MessageActionReceived</c>, <c>Error</c>. The init setter rejects any other value
    /// with <see cref="ArgumentException"/>.
    /// </summary>
    public required string EventType
    {
        get => _eventType;
        init
        {
            if (!AuditEventTypes.IsValid(value))
            {
                throw new ArgumentException(
                    $"'{value}' is not a canonical audit EventType. Allowed values: " +
                    $"[{string.Join(", ", AuditEventTypes.All)}].",
                    nameof(EventType));
            }

            _eventType = value;
        }
    }

    /// <summary>Actor identity — AAD object ID for users, agent ID for agent-originated events.</summary>
    public required string ActorId { get; init; }

    /// <summary>
    /// One of <see cref="AuditActorTypes.All"/>: <c>User</c> or <c>Agent</c>. The init
    /// setter rejects any other value with <see cref="ArgumentException"/>.
    /// </summary>
    public required string ActorType
    {
        get => _actorType;
        init
        {
            if (!AuditActorTypes.IsValid(value))
            {
                throw new ArgumentException(
                    $"'{value}' is not a canonical audit ActorType. Allowed values: " +
                    $"[{string.Join(", ", AuditActorTypes.All)}].",
                    nameof(ActorType));
            }

            _actorType = value;
        }
    }

    /// <summary>Entra ID tenant of the actor.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// The agent whose task or question triggered this event. Present on every event tied to
    /// an agent task — including human-originated actions such as <c>approve</c> or
    /// <c>reject</c> — so the associated agent is recorded even when
    /// <see cref="ActorType"/> = <see cref="AuditActorTypes.User"/>. Null for events that are
    /// not associated with a specific agent (for example, <c>agent status</c> queries or
    /// security rejections).
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>Agent task or work-item ID; null for events outside a task context (such as security rejection).</summary>
    public string? TaskId { get; init; }

    /// <summary>Teams (or other messenger) conversation ID; null for events outside a conversation.</summary>
    public string? ConversationId { get; init; }

    /// <summary>The specific action taken (for example, <c>approve</c>, <c>reject</c>, <c>agent ask</c>, <c>send_card</c>).</summary>
    public required string Action { get; init; }

    /// <summary>JSON-serialized event payload (sanitized — no secrets or PII beyond identity).</summary>
    public required string PayloadJson { get; init; }

    /// <summary>
    /// One of <see cref="AuditOutcomes.All"/>: <c>Success</c>, <c>Rejected</c>,
    /// <c>Failed</c>, <c>DeadLettered</c>. The init setter rejects any other value with
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public required string Outcome
    {
        get => _outcome;
        init
        {
            if (!AuditOutcomes.IsValid(value))
            {
                throw new ArgumentException(
                    $"'{value}' is not a canonical audit Outcome. Allowed values: " +
                    $"[{string.Join(", ", AuditOutcomes.All)}].",
                    nameof(Outcome));
            }

            _outcome = value;
        }
    }

    /// <summary>
    /// SHA-256 digest (lower-case hex) over the canonical fields for tamper detection.
    /// Compute with <see cref="ComputeChecksum"/> using the same field values used to
    /// construct this entry; downstream audit storage verifies the digest matches the
    /// persisted columns before treating a row as authentic.
    /// </summary>
    public required string Checksum { get; init; }

    /// <summary>
    /// Compute the canonical SHA-256 checksum for the supplied audit fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The digest is computed over a length-prefixed binary representation of every
    /// canonical field — each field is serialized as a little-endian 32-bit byte-length
    /// prefix followed by the UTF-8 bytes of the value (with a <c>-1</c> sentinel for null
    /// nullable fields). <see cref="DateTimeOffset"/> is first formatted as ISO-8601
    /// round-trip (<c>"O"</c>) under <see cref="CultureInfo.InvariantCulture"/>, then
    /// length-prefixed as a string.
    /// </para>
    /// <para>
    /// Length-prefixing avoids the delimiter-collision attack that plagues
    /// separator-joined canonical encodings: because every field carries its own explicit
    /// byte length, no payload content (including pipe characters, newlines, or any other
    /// byte sequence) can shift across field boundaries to forge a different field
    /// decomposition that hashes identically.
    /// </para>
    /// <para>
    /// The digest is returned as lower-case hex.
    /// </para>
    /// </remarks>
    /// <param name="timestamp">Same value as <see cref="Timestamp"/>.</param>
    /// <param name="correlationId">Same value as <see cref="CorrelationId"/>.</param>
    /// <param name="eventType">Same value as <see cref="EventType"/>.</param>
    /// <param name="actorId">Same value as <see cref="ActorId"/>.</param>
    /// <param name="actorType">Same value as <see cref="ActorType"/>.</param>
    /// <param name="tenantId">Same value as <see cref="TenantId"/>.</param>
    /// <param name="agentId">Same value as <see cref="AgentId"/>.</param>
    /// <param name="taskId">Same value as <see cref="TaskId"/>.</param>
    /// <param name="conversationId">Same value as <see cref="ConversationId"/>.</param>
    /// <param name="action">Same value as <see cref="Action"/>.</param>
    /// <param name="payloadJson">Same value as <see cref="PayloadJson"/>.</param>
    /// <param name="outcome">Same value as <see cref="Outcome"/>.</param>
    public static string ComputeChecksum(
        DateTimeOffset timestamp,
        string correlationId,
        string eventType,
        string actorId,
        string actorType,
        string tenantId,
        string? agentId,
        string? taskId,
        string? conversationId,
        string action,
        string payloadJson,
        string outcome)
    {
        if (correlationId is null) throw new ArgumentNullException(nameof(correlationId));
        if (eventType is null) throw new ArgumentNullException(nameof(eventType));
        if (actorId is null) throw new ArgumentNullException(nameof(actorId));
        if (actorType is null) throw new ArgumentNullException(nameof(actorType));
        if (tenantId is null) throw new ArgumentNullException(nameof(tenantId));
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (payloadJson is null) throw new ArgumentNullException(nameof(payloadJson));
        if (outcome is null) throw new ArgumentNullException(nameof(outcome));

        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            // 32-bit version tag so future schema changes are distinguishable.
            writer.Write(CanonicalEncodingVersion);

            WriteField(writer, timestamp.ToString("O", CultureInfo.InvariantCulture));
            WriteField(writer, correlationId);
            WriteField(writer, eventType);
            WriteField(writer, actorId);
            WriteField(writer, actorType);
            WriteField(writer, tenantId);
            WriteNullableField(writer, agentId);
            WriteNullableField(writer, taskId);
            WriteNullableField(writer, conversationId);
            WriteField(writer, action);
            WriteField(writer, payloadJson);
            WriteField(writer, outcome);
        }

        var digest = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Version tag prefixed onto the canonical encoding so any future change to the field
    /// layout produces a different digest (and a test can fail loudly rather than silently
    /// accepting a stale checksum).
    /// </summary>
    private const int CanonicalEncodingVersion = 1;

    private static void WriteField(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableField(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            // Sentinel byte length distinguishes null from empty string.
            writer.Write(-1);
            return;
        }

        WriteField(writer, value);
    }
}
