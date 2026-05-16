using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// Discriminator + payload pair persisted into <c>OutboxEntry.PayloadJson</c>. The
/// <c>OutboxEntry.PayloadType</c> column already carries the discriminator independently
/// (so <see cref="TeamsOutboxDispatcher"/> can pre-route without parsing JSON), but the
/// envelope keeps both <see cref="MessengerMessage"/> and <see cref="AgentQuestion"/>
/// shapes addressable under a single typed contract for the wire format.
/// </summary>
/// <remarks>
/// <para>
/// Stored as a JSON object with exactly one of <see cref="MessengerMessage"/> or
/// <see cref="AgentQuestion"/> populated. The deserializer is tolerant — null on the
/// other field is the expected shape, not an error. <see cref="JsonOptions"/> is the
/// single shared option set so the encode (decorator) and decode (dispatcher) sides
/// agree on casing.
/// </para>
/// <para>
/// We intentionally do NOT embed <c>ConversationReferenceJson</c> in this envelope —
/// that field has its own first-class column on <see cref="Core.OutboxEntry"/> so the
/// outbox row remains self-describing without parsing the payload.
/// </para>
/// </remarks>
public sealed class TeamsOutboxPayloadEnvelope
{
    /// <summary>Canonical serializer options — camelCase, ignore-null, no indentation.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Populated for <c>PayloadType = MessengerMessage</c> entries.</summary>
    public MessengerMessage? Message { get; set; }

    /// <summary>Populated for <c>PayloadType = AgentQuestion</c> entries.</summary>
    public AgentQuestion? Question { get; set; }
}
