namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Platform-agnostic outbound message produced by an agent for delivery to a human via any
/// messenger connector (Teams, Slack, Discord, Telegram). Carries the canonical correlation
/// envelope mandated by FR-004 so every outbound message is end-to-end traceable.
/// </summary>
/// <param name="MessageId">Unique message identifier.</param>
/// <param name="CorrelationId">End-to-end trace ID linking the message to the originating task.</param>
/// <param name="AgentId">Identity of the agent that produced the message.</param>
/// <param name="TaskId">Identifier of the associated task or work item.</param>
/// <param name="ConversationId">Target human conversation or thread.</param>
/// <param name="Body">Message body (typically markdown).</param>
/// <param name="Severity">One of <see cref="MessageSeverities"/>: <c>Info</c>, <c>Warning</c>, <c>Error</c>, <c>Critical</c>.</param>
/// <param name="Timestamp">UTC creation time.</param>
public sealed record MessengerMessage(
    string MessageId,
    string CorrelationId,
    string AgentId,
    string TaskId,
    string ConversationId,
    string Body,
    string Severity,
    DateTimeOffset Timestamp);
