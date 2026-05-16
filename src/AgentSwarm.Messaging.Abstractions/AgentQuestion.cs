namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A blocking question raised by an agent that requires human input before
/// the agent can proceed. Rendered into platform-native interactive
/// controls (Block Kit on Slack, inline keyboards on Telegram, components
/// on Discord, adaptive cards on Teams).
/// </summary>
/// <remarks>
/// COMPILE STUB. Field contract mirrors section 3.6.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/architecture.md</c>:
/// <c>AgentQuestion(QuestionId, AgentId, TaskId, Title, Body, Severity,
/// AllowedActions, ExpiresAt, CorrelationId)</c>.
/// <para>
/// <c>Severity</c> is typed as <see cref="string"/> here to avoid pinning a
/// concrete enum shape ahead of the upstream Abstractions story; the Slack
/// renderer maps the string (e.g., "critical", "warning", "info") to a
/// color bar and emoji prefix.
/// </para>
/// </remarks>
/// <param name="QuestionId">Stable identifier for this question.</param>
/// <param name="AgentId">Identity of the agent that raised the question.</param>
/// <param name="TaskId">Work-item identifier the question relates to.</param>
/// <param name="Title">Short headline rendered above the body.</param>
/// <param name="Body">Full question text shown to the human.</param>
/// <param name="Severity">Severity bucket ("info" / "warning" / "critical") used for styling.</param>
/// <param name="AllowedActions">Set of buttons / modal triggers the human can choose from.</param>
/// <param name="ExpiresAt">UTC deadline after which the question is considered abandoned.</param>
/// <param name="CorrelationId">End-to-end correlation id propagated from agent to messenger and back.</param>
public sealed record AgentQuestion(
    string QuestionId,
    string AgentId,
    string TaskId,
    string Title,
    string Body,
    string Severity,
    IReadOnlyList<HumanAction> AllowedActions,
    DateTimeOffset ExpiresAt,
    string CorrelationId);
