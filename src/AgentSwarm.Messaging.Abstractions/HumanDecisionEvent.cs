namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Emitted when a human responds to an <see cref="AgentQuestion"/> by selecting one of its
/// <see cref="HumanAction"/> buttons. Wrapped inside a <see cref="DecisionEvent"/> when
/// flowing through the inbound event channel.
/// </summary>
/// <param name="QuestionId">Identifier of the answered question.</param>
/// <param name="ActionValue">The selected action's machine-readable <see cref="HumanAction.Value"/>.</param>
/// <param name="Comment">Optional free-text comment supplied by the human (null when the action did not require a comment).</param>
/// <param name="Messenger">Source messenger ("Teams", "Slack", "Discord", "Telegram").</param>
/// <param name="ExternalUserId">External user identifier for the responder (AAD object ID for Teams).</param>
/// <param name="ExternalMessageId">External activity/message ID of the user's response.</param>
/// <param name="ReceivedAt">UTC time the gateway received the response.</param>
/// <param name="CorrelationId">End-to-end trace ID propagated from the originating question.</param>
public sealed record HumanDecisionEvent(
    string QuestionId,
    string ActionValue,
    string? Comment,
    string Messenger,
    string ExternalUserId,
    string ExternalMessageId,
    DateTimeOffset ReceivedAt,
    string CorrelationId);
