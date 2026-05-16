namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Rendered outbound Slack Web API call buffered by
/// <see cref="Queues.ISlackOutboundQueue"/> and consumed by the
/// <c>SlackOutboundDispatcher</c> background service. Carries everything
/// the dispatcher needs to issue the API call without re-rendering the
/// payload.
/// </summary>
/// <remarks>
/// COMPILE STUB introduced by Stage 1.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. The
/// canonical field surface is owned by Stage 4.1 (Slack Outbound
/// Dispatcher) line 355 of <c>implementation-plan.md</c>, which spells out
/// exactly these fields:
/// <c>TaskId, CorrelationId, MessageType, BlockKitPayload, ThreadTs</c>.
/// Defining the record here lets the queue contracts compile without
/// forcing Stage 4.1 to revise the field list. The brief's
/// <c>MessageType</c> field name is preserved verbatim; its typed value is
/// a <see cref="SlackOutboundOperationKind"/> to avoid colliding with
/// <see cref="AgentSwarm.Messaging.Abstractions.MessageType"/>.
/// </remarks>
/// <param name="TaskId">Work-item identifier the message belongs to. Used by the dispatcher to resolve the thread mapping and by the audit logger for correlation.</param>
/// <param name="CorrelationId">End-to-end correlation id propagated from agent through messenger and back.</param>
/// <param name="MessageType">Slack Web API verb to invoke (post / update / views.update).</param>
/// <param name="BlockKitPayload">Pre-rendered Block Kit JSON ready to be sent to Slack.</param>
/// <param name="ThreadTs">
/// Slack thread timestamp the message should be posted into. <c>null</c>
/// when the message starts a new top-level post (the dispatcher will create
/// the thread on first send via <c>SlackThreadManager</c>).
/// </param>
internal sealed record SlackOutboundEnvelope(
    string TaskId,
    string CorrelationId,
    SlackOutboundOperationKind MessageType,
    string BlockKitPayload,
    string? ThreadTs);
