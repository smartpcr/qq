namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Inbound event raised when a human responds to an
/// <see cref="AgentQuestion"/> via a messenger interaction (button click or
/// modal submission). Produced by the connector's interaction handler and
/// published to the orchestrator.
/// </summary>
/// <remarks>
/// COMPILE STUB. Field contract mirrors section 3.6.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/architecture.md</c>:
/// <c>HumanDecisionEvent(QuestionId, ActionValue, Comment, Messenger,
/// ExternalUserId, ExternalMessageId, ReceivedAt, CorrelationId)</c>.
/// For the Slack connector, <see cref="Messenger"/> is always
/// <c>"slack"</c>.
/// </remarks>
/// <param name="QuestionId">Identifier of the originating <see cref="AgentQuestion"/>.</param>
/// <param name="ActionValue">Machine-readable value of the chosen <see cref="HumanAction"/>.</param>
/// <param name="Comment">Optional free-form text supplied through a comment modal.</param>
/// <param name="Messenger">Source messenger platform ("slack", "telegram", ...).</param>
/// <param name="ExternalUserId">Platform-specific user identifier (e.g., Slack user id).</param>
/// <param name="ExternalMessageId">Platform-specific message identifier (e.g., Slack <c>ts</c>).</param>
/// <param name="ReceivedAt">UTC timestamp at which the connector observed the interaction.</param>
/// <param name="CorrelationId">End-to-end correlation id resolved from the originating question / thread mapping.</param>
public sealed record HumanDecisionEvent(
    string QuestionId,
    string ActionValue,
    string? Comment,
    string Messenger,
    string ExternalUserId,
    string ExternalMessageId,
    DateTimeOffset ReceivedAt,
    string CorrelationId) : MessengerEvent;
