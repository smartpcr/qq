namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A question raised by an agent that requires a human decision. Shared across
/// every messenger connector (Discord, Telegram, Slack, Teams). See architecture.md
/// Section 3.1 for the full field semantics and the FR-001 epic brief for the
/// originating contract.
/// </summary>
/// <param name="QuestionId">
/// Unique question identifier. Constrained to printable ASCII, max
/// <see cref="QuestionIdValidator.MaxLength"/> characters, no <c>:</c> separator.
/// Validate with <see cref="QuestionIdValidator"/>.
/// </param>
/// <param name="AgentId">Originating agent identifier.</param>
/// <param name="TaskId">Associated work item / task identifier.</param>
/// <param name="Title">Short summary rendered as the message subject.</param>
/// <param name="Body">Full context displayed to the human operator.</param>
/// <param name="Severity">Priority severity (drives queue ordering).</param>
/// <param name="AllowedActions">
/// Operator-selectable actions. Rendered as buttons (Discord, Slack, Teams) or as
/// an inline keyboard (Telegram).
/// </param>
/// <param name="ExpiresAt">Deadline after which the question is considered timed out.</param>
/// <param name="CorrelationId">End-to-end trace identifier.</param>
public sealed record AgentQuestion(
    string QuestionId,
    string AgentId,
    string TaskId,
    string Title,
    string Body,
    MessageSeverity Severity,
    HumanAction[] AllowedActions,
    DateTimeOffset ExpiresAt,
    string CorrelationId);
