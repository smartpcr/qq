namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Canonical key constants for the data dictionary that flows in and out of an Adaptive
/// Card <c>Action.Submit</c>. Production code on both sides — <see cref="AdaptiveCardBuilder"/>
/// when building the card and <see cref="CardActionMapper"/> when interpreting the inbound
/// activity — uses these constants so a typo on either side fails at compile time rather
/// than silently dropping a field.
/// </summary>
public static class CardActionDataKeys
{
    /// <summary>Originating <c>AgentQuestion.QuestionId</c>. Required on every action payload.</summary>
    public const string QuestionId = "questionId";

    /// <summary>
    /// Unique <see cref="AgentSwarm.Messaging.Abstractions.HumanAction.ActionId"/> of the
    /// pressed button, scoped to the parent question. Required on every action payload.
    /// Distinguishes button identity from the machine-readable
    /// <see cref="AgentSwarm.Messaging.Abstractions.HumanAction.Value"/> so that
    /// Stage 3.3's <c>CardActionHandler</c> can resolve the exact action even if two
    /// actions on different questions happen to share the same value (per
    /// <c>architecture.md</c> §2.10 and §6.3 step 4).
    /// </summary>
    public const string ActionId = "actionId";

    /// <summary>The selected <c>HumanAction.Value</c>. Required on every action payload.</summary>
    public const string ActionValue = "actionValue";

    /// <summary>End-to-end correlation ID propagated from the originating question. Required.</summary>
    public const string CorrelationId = "correlationId";

    /// <summary>
    /// Optional free-text comment supplied by the user via the rendered
    /// <c>Input.Text</c> field. Present only when at least one of the question's
    /// <c>AllowedActions</c> declares <c>RequiresComment = true</c>; even then the value
    /// may be empty if the user pressed an action that does not require a comment.
    /// </summary>
    public const string Comment = "comment";
}

/// <summary>
/// Strongly-typed view of the data dictionary attached to an Adaptive Card
/// <c>Action.Submit</c> payload. The card builder uses this type to populate
/// <c>AdaptiveSubmitAction.Data</c>; the mapper deserialises the inbound
/// <see cref="Microsoft.Bot.Schema.Activity.Value"/> into it before assembling the
/// <see cref="AgentSwarm.Messaging.Abstractions.HumanDecisionEvent"/>.
/// </summary>
/// <param name="QuestionId">Originating <c>AgentQuestion.QuestionId</c>.</param>
/// <param name="ActionId">The pressed button's unique <see cref="AgentSwarm.Messaging.Abstractions.HumanAction.ActionId"/>.</param>
/// <param name="ActionValue">The selected <c>HumanAction.Value</c>.</param>
/// <param name="CorrelationId">End-to-end correlation ID propagated from the question.</param>
/// <param name="Comment">Optional free-text comment.</param>
public sealed record CardActionPayload(
    string QuestionId,
    string ActionId,
    string ActionValue,
    string CorrelationId,
    string? Comment);
