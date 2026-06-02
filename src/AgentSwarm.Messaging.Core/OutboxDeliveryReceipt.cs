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
    DateTimeOffset DeliveredAt)
{
    /// <summary>
    /// Stage 6.1 (iter-4 evaluator feedback) — serialized Bot Framework
    /// <c>ConversationReference</c> captured from
    /// <c>turnContext.Activity.GetConversationReference()</c> AFTER the proactive
    /// <c>ContinueConversationAsync</c> → <c>SendActivityAsync</c> call. The
    /// post-send reference can differ from the enqueue-time reference resolved from
    /// <c>IConversationReferenceStore</c> (e.g. <c>ServiceUrl</c> rewrites,
    /// regional routing, <c>Conversation.Id</c> reshuffling); persisting the
    /// DELIVERED reference durably onto the outbox row ensures the dispatcher's
    /// Layer-1 idempotent-replay path
    /// (<c>TeamsOutboxDispatcher.DispatchQuestionAsync</c>) — and any subsequent
    /// <c>ITeamsCardManager</c> update/delete that rehydrates from the row's
    /// <see cref="OutboxEntry.ConversationReferenceJson"/> — addresses the SAME
    /// conversation the original send actually reached, not a stale enqueue-time
    /// snapshot. <c>null</c> for plain <c>MessengerMessage</c> payloads (no
    /// AgentQuestion capture semantics) or when the dispatcher could not serialize
    /// the post-send reference.
    /// </summary>
    public string? ConversationReferenceJson { get; init; }
}
