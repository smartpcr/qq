namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Envelope record returned by <see cref="IMessageStore.GetByCorrelationIdAsync"/> for
/// both inbound and outbound persisted messages. Mirrors the columns of the future SQL
/// <c>Messages</c> table so a row maps 1:1 onto an instance without a separate adapter.
/// </summary>
/// <param name="MessageId">Unique message identifier (matches the source record's <c>MessageId</c> or <c>EventId</c>).</param>
/// <param name="CorrelationId">End-to-end trace ID propagated from the originating activity.</param>
/// <param name="Direction">One of <see cref="MessageDirections.All"/>: <c>Inbound</c> or <c>Outbound</c>.</param>
/// <param name="Timestamp">UTC time the message was received (inbound) or queued (outbound).</param>
/// <param name="Messenger">Source/target messenger (for example, <c>"Teams"</c>); null when not applicable.</param>
/// <param name="PayloadType">CLR type name of the serialized payload (for example, <c>"MessengerMessage"</c>, <c>"CommandEvent"</c>, <c>"DecisionEvent"</c>).</param>
/// <param name="PayloadJson">JSON-serialized payload — the raw inbound event or outbound message body.</param>
public sealed record PersistedMessage(
    string MessageId,
    string CorrelationId,
    string Direction,
    DateTimeOffset Timestamp,
    string? Messenger,
    string PayloadType,
    string PayloadJson);
