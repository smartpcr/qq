namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Generic outbound message produced by an agent and delivered through a
/// messenger connector. Rendered into the platform's native representation
/// (Block Kit for Slack, MessageEntities for Telegram, etc.) before being
/// posted to the task thread.
/// </summary>
/// <remarks>
/// COMPILE STUB. The canonical type is owned by the upstream Abstractions
/// story. Property names mirror section 3.6.4 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/architecture.md</c>:
/// <c>MessengerMessage(MessageId, AgentId, TaskId, Content, MessageType,
/// CorrelationId, Timestamp)</c>.
/// </remarks>
/// <param name="MessageId">Unique identifier for this outbound message.</param>
/// <param name="AgentId">Identity of the agent that produced the message.</param>
/// <param name="TaskId">Work-item identifier that scopes the message.</param>
/// <param name="Content">Human-readable body to be rendered.</param>
/// <param name="MessageType">Classification used by the renderer to style the post.</param>
/// <param name="CorrelationId">End-to-end correlation id propagated from agent to messenger.</param>
/// <param name="Timestamp">UTC timestamp at which the message was produced.</param>
public sealed record MessengerMessage(
    string MessageId,
    string AgentId,
    string TaskId,
    string Content,
    MessageType MessageType,
    string CorrelationId,
    DateTimeOffset Timestamp);
