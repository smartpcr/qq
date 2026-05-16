using System.Text.Json.Serialization;
using AgentSwarm.Messaging.Abstractions.Json;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Classifies the originating event behind an <see cref="OutboundMessage"/>.
/// Drives the <c>IdempotencyKey</c> prefix and the rendering path the
/// connector takes on send (questions render component shells; alerts render
/// embed templates; status updates may be batched).
/// </summary>
/// <remarks>
/// Serialized as the member name string via
/// <see cref="OutboundMessageSourceJsonConverter"/> — names-only wire contract.
/// </remarks>
[JsonConverter(typeof(OutboundMessageSourceJsonConverter))]
public enum OutboundMessageSource
{
    /// <summary>An <see cref="AgentQuestion"/> awaiting human decision.</summary>
    Question = 0,

    /// <summary>A high-priority operator alert.</summary>
    Alert = 1,

    /// <summary>A periodic agent progress / status update.</summary>
    StatusUpdate = 2,

    /// <summary>An acknowledgement for a slash command invocation.</summary>
    CommandAck = 3,
}
