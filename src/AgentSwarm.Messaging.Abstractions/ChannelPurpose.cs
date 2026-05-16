using System.Text.Json.Serialization;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Logical purpose of a messenger channel bound to a swarm tenant.
/// Used by <see cref="GuildBinding"/> to route outbound traffic to
/// the right channel (per architecture.md Section 3.1).
/// Serialized as the member name string in JSON.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelPurpose
{
    /// <summary>Receives commands and agent questions.</summary>
    Control = 0,

    /// <summary>Receives priority alerts.</summary>
    Alert = 1,

    /// <summary>Receives task-specific updates and threads.</summary>
    Workstream = 2,
}
