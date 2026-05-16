using System.Text.Json.Serialization;
using AgentSwarm.Messaging.Abstractions.Json;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Lifecycle state of an <see cref="OutboundMessage"/> as it progresses through
/// the durable outbound pipeline. See architecture.md Section 3.2 and Section 10.3.
/// </summary>
/// <remarks>
/// Wire format: serialized as the member name string via
/// <see cref="OutboundMessageStatusJsonConverter"/> for the same reasons as
/// <see cref="MessageSeverity"/> — names-only contract, integers stable for
/// in-process comparisons but not externalised.
/// </remarks>
[JsonConverter(typeof(OutboundMessageStatusJsonConverter))]
public enum OutboundMessageStatus
{
    /// <summary>Persisted to the outbox; awaiting dispatcher pickup.</summary>
    Pending = 0,

    /// <summary>Picked up by the dispatcher; in-flight to the platform API.</summary>
    Sending = 1,

    /// <summary>Delivered to the platform; <c>PlatformMessageId</c> populated.</summary>
    Sent = 2,

    /// <summary>Last attempt failed; awaiting retry per <c>NextRetryAt</c>.</summary>
    Failed = 3,

    /// <summary>
    /// Exhausted <c>MaxAttempts</c>; moved to dead-letter store for operator
    /// triage. Not retried automatically.
    /// </summary>
    DeadLettered = 4,
}
