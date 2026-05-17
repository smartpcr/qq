namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Bookkeeping fields the outbox dispatcher hands back to the engine on a successful
/// delivery so the durable audit row carries the Teams ActivityId/ConversationId. The
/// architecture treats these IDs as part of the "immutable audit trail suitable for
/// enterprise review" (story §Compliance) — they must land on the outbox row itself even
/// if a downstream <c>ICardStateStore</c> write fails.
/// </summary>
/// <param name="ActivityId">Teams <c>ResourceResponse.Id</c> from the proactive send, or
/// <c>null</c> when the payload is a plain <c>MessengerMessage</c> that does not warrant
/// card-state tracking.</param>
/// <param name="ConversationId">Teams <c>Activity.Conversation.Id</c> from the proactive
/// turn context.</param>
/// <param name="DeliveredAt">UTC time the delivery completed.</param>
public readonly record struct OutboxDeliveryReceipt(
    string? ActivityId,
    string? ConversationId,
    DateTimeOffset DeliveredAt);
