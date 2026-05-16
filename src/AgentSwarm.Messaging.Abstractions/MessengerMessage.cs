namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Generic outbound message envelope for non-question traffic (status updates,
/// alerts, command acknowledgements). Consumed by
/// <c>IMessengerConnector.SendMessageAsync</c>.
/// </summary>
/// <param name="Messenger">
/// Messenger identifier (<c>"Discord"</c>, <c>"Telegram"</c>, <c>"Slack"</c>,
/// <c>"Teams"</c>).
/// </param>
/// <param name="ChannelId">
/// Connector-native channel identifier (Discord channel snowflake stringified,
/// Telegram chat id, Slack channel id, Teams conversation id).
/// </param>
/// <param name="Body">Rendered message body or serialized connector-native payload.</param>
/// <param name="Severity">Priority severity used for queue ordering.</param>
/// <param name="CorrelationId">End-to-end trace identifier.</param>
/// <param name="Metadata">
/// Optional connector-specific routing or rendering hints (e.g. <c>ThreadId</c>,
/// <c>EmbedJson</c>, <c>AgentId</c>). Keys are case-sensitive. The caller-supplied
/// dictionary is defensively copied at construction into a read-only wrapper that
/// cannot be downcast back to <see cref="Dictionary{TKey, TValue}"/>.
/// </param>
public sealed record MessengerMessage(
    string Messenger,
    string ChannelId,
    string Body,
    MessageSeverity Severity,
    string CorrelationId,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    private readonly IReadOnlyDictionary<string, string>? _metadata =
        ImmutableSnapshot.FromStringMap(Metadata, nameof(Metadata));

    /// <inheritdoc cref="MessengerMessage(string, string, string, MessageSeverity, string, IReadOnlyDictionary{string, string}?)"/>
    public IReadOnlyDictionary<string, string>? Metadata
    {
        get => _metadata;
        init => _metadata = ImmutableSnapshot.FromStringMap(value, nameof(Metadata));
    }
}

