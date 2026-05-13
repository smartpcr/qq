using System.ComponentModel.DataAnnotations;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// An inbound event recording the decision a human made on an
/// <see cref="AgentQuestion"/>. Produced by the messenger connector and consumed
/// by the orchestrator to unblock the waiting agent.
/// </summary>
public sealed record HumanDecisionEvent
{
    /// <summary>Id of the question the decision answers.</summary>
    [Required(AllowEmptyStrings = false)]
    public string QuestionId { get; init; } = string.Empty;

    /// <summary>The <see cref="HumanAction.Value"/> selected by the human.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ActionValue { get; init; } = string.Empty;

    /// <summary>Optional free-text comment captured alongside the action.</summary>
    public string? Comment { get; init; }

    /// <summary>Messenger that delivered the decision (e.g. <c>Teams</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Messenger { get; init; } = string.Empty;

    /// <summary>Messenger-native user id of the human (e.g. AAD object id).</summary>
    [Required(AllowEmptyStrings = false)]
    public string ExternalUserId { get; init; } = string.Empty;

    /// <summary>Messenger-native message id of the inbound activity.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ExternalMessageId { get; init; } = string.Empty;

    /// <summary>UTC timestamp the connector received the activity.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Correlation id propagated from the originating question.</summary>
    [Required(AllowEmptyStrings = false)]
    public string CorrelationId { get; init; } = string.Empty;
}
