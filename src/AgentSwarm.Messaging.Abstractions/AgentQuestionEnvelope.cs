namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Wraps an <see cref="AgentQuestion"/> with routing and context metadata.
/// The envelope is the unit of transport through IMessengerConnector.SendQuestionAsync.
/// </summary>
/// <remarks>
/// Construction-time validation enforces the invariant that
/// <see cref="ProposedDefaultActionId"/> — when supplied — must match the
/// <see cref="HumanAction.ActionId"/> of one entry in
/// <c>Question.AllowedActions</c>. If a downstream component were to render
/// or persist a default that did not correspond to any allowed action, the
/// timeout / default-action handlers would publish a
/// <see cref="HumanDecisionEvent.ActionValue"/> for which the agent has no
/// corresponding branch, silently dropping the decision. Validating at
/// construction time fails fast at the connector boundary instead.
/// Validation is order-independent: the check runs whenever either
/// property is set, but only fires once <see cref="Question"/> is present.
/// </remarks>
public sealed record AgentQuestionEnvelope
{
    private readonly AgentQuestion _question = null!;
    private readonly string? _proposedDefaultActionId;
    private bool _questionSet;

    public required AgentQuestion Question
    {
        get => _question;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(Question));
            _question = value;
            _questionSet = true;
            ValidateDefaultActionAgainstAllowed();
        }
    }

    /// <summary>
    /// The ActionId from AllowedActions to apply automatically on timeout.
    /// When null, the question expires with ActionValue = "__timeout__".
    /// When non-null, must match the ActionId of one entry in
    /// <c>Question.AllowedActions</c> — validated at construction time.
    /// </summary>
    public string? ProposedDefaultActionId
    {
        get => _proposedDefaultActionId;
        init
        {
            _proposedDefaultActionId = value;
            ValidateDefaultActionAgainstAllowed();
        }
    }

    public IReadOnlyDictionary<string, string> RoutingMetadata { get; init; } =
        new Dictionary<string, string>();

    private void ValidateDefaultActionAgainstAllowed()
    {
        if (!_questionSet) return;
        if (_proposedDefaultActionId is null) return;

        foreach (var action in _question.AllowedActions)
        {
            if (string.Equals(action.ActionId, _proposedDefaultActionId, StringComparison.Ordinal))
            {
                return;
            }
        }

        var available = _question.AllowedActions.Count == 0
            ? "(none — AllowedActions is empty)"
            : string.Join(", ", _question.AllowedActions.Select(a => $"'{a.ActionId}'"));
        throw new ArgumentException(
            $"ProposedDefaultActionId '{_proposedDefaultActionId}' must match the "
            + $"ActionId of one entry in Question.AllowedActions; available: {available}. "
            + "An invalid default would cause timeout / default-action handlers to publish "
            + "a HumanDecisionEvent.ActionValue with no matching agent branch.",
            nameof(ProposedDefaultActionId));
    }
}
