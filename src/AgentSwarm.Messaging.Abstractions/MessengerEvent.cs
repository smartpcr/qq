using System.Text.Json.Serialization;
using AgentSwarm.Messaging.Abstractions.Json;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Classification of an inbound <see cref="MessengerEvent"/>.
/// Serialized as the member name string in JSON via
/// <see cref="MessengerEventTypeJsonConverter"/>; the wire contract is
/// <em>names-only</em> (numeric tokens and numeric strings are rejected).
/// </summary>
[JsonConverter(typeof(MessengerEventTypeJsonConverter))]
public enum MessengerEventType
{
    /// <summary>A slash command / typed command invocation.</summary>
    Command = 0,

    /// <summary>A button click on a previously sent message component.</summary>
    ButtonClick = 1,

    /// <summary>A select-menu selection on a previously sent message component.</summary>
    SelectMenu = 2,

    /// <summary>A modal/form submission.</summary>
    ModalSubmit = 3,

    /// <summary>A plain text message addressed at the bot.</summary>
    Message = 4,
}

/// <summary>
/// Generic inbound event envelope produced by a messenger connector's gateway
/// (Discord <c>InteractionCreated</c>, Telegram update, Slack event, Teams
/// activity) before connector-specific dispatch.
/// </summary>
/// <param name="Messenger">
/// Messenger identifier (<c>"Discord"</c>, <c>"Telegram"</c>, <c>"Slack"</c>,
/// <c>"Teams"</c>).
/// </param>
/// <param name="EventType">Classification of the inbound event.</param>
/// <param name="ExternalUserId">Connector-native id of the user who triggered the event.</param>
/// <param name="ExternalChannelId">Connector-native id of the channel/chat/conversation.</param>
/// <param name="ExternalMessageId">
/// Connector-native id of the interaction, callback query, or message that triggered
/// the event.
/// </param>
/// <param name="Payload">
/// Optional connector-native payload (raw text, serialized component data, modal
/// values). Connectors are free to attach the JSON-serialized native event here.
/// </param>
/// <param name="CorrelationId">Trace identifier assigned by the gateway pipeline.</param>
/// <param name="Timestamp">When the event was received from the messenger platform.</param>
/// <param name="Metadata">
/// Optional connector-specific extras (e.g. <c>GuildId</c>, <c>ThreadId</c>,
/// <c>InteractionToken</c>). Keys are case-sensitive. The caller-supplied
/// dictionary is defensively copied at construction into a read-only wrapper
/// that cannot be downcast back to <see cref="Dictionary{TKey, TValue}"/>.
/// </param>
public sealed record MessengerEvent(
    string Messenger,
    MessengerEventType EventType,
    string ExternalUserId,
    string ExternalChannelId,
    string ExternalMessageId,
    string? Payload,
    string CorrelationId,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    private readonly IReadOnlyDictionary<string, string>? _metadata =
        ImmutableSnapshot.FromStringMap(Metadata, nameof(Metadata));

    /// <inheritdoc cref="MessengerEvent(string, MessengerEventType, string, string, string, string?, string, DateTimeOffset, IReadOnlyDictionary{string, string}?)"/>
    public IReadOnlyDictionary<string, string>? Metadata
    {
        get => _metadata;
        init => _metadata = ImmutableSnapshot.FromStringMap(value, nameof(Metadata));
    }
}
