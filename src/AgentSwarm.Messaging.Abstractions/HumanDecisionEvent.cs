namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A human operator's resolution of an <see cref="AgentQuestion"/>. Emitted by the
/// connector after a button click, select-menu choice, or modal submission, and
/// published to the orchestrator via <c>ISwarmCommandBus.PublishHumanDecisionAsync</c>.
/// </summary>
/// <param name="Messenger">
/// Messenger identifier. For the Discord connector this is always <c>"Discord"</c>;
/// other connectors populate <c>"Telegram"</c>, <c>"Slack"</c>, or <c>"Teams"</c>.
/// </param>
/// <param name="ExternalUserId">
/// Messenger-native user id of the responding operator (stringified snowflake for
/// Discord, Telegram user id, Slack member id, AAD object id for Teams).
/// </param>
/// <param name="ExternalMessageId">
/// Messenger-native id of the interaction or message that produced the decision
/// (Discord interaction snowflake, Telegram callback query id, etc.).
/// </param>
/// <param name="QuestionId">Identifier of the <see cref="AgentQuestion"/> being answered.</param>
/// <param name="SelectedActionId">
/// <see cref="HumanAction.ActionId"/> chosen by the operator.
/// </param>
/// <param name="ActionValue">
/// Resolved <see cref="HumanAction.Value"/> for the selected action.
/// </param>
/// <param name="CorrelationId">Trace identifier propagated from the originating question.</param>
/// <param name="Timestamp">When the operator's decision was received.</param>
/// <param name="Comment">
/// Optional free-form text supplied by the operator. Required (non-null) when the
/// chosen <see cref="HumanAction.RequiresComment"/> is <see langword="true"/>; the
/// connector collects the text via a modal/follow-up reply and forwards it here so
/// the orchestrator receives the rationale alongside the decision. <see langword="null"/>
/// when no comment was requested or provided. Default <see langword="null"/> keeps
/// the parameter additive — existing positional callers do not break.
/// </param>
public sealed record HumanDecisionEvent(
    string Messenger,
    string ExternalUserId,
    string ExternalMessageId,
    string QuestionId,
    string SelectedActionId,
    string ActionValue,
    string CorrelationId,
    DateTimeOffset Timestamp,
    string? Comment = null);
