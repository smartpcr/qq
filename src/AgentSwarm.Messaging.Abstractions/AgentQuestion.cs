using System.ComponentModel.DataAnnotations;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A blocking question raised by an agent that requires a human decision via a
/// messenger card. Persisted by <c>IAgentQuestionStore</c> (Stage 3.3) and rendered
/// proactively by <c>TeamsMessengerConnector</c>.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="TargetUserId"/> or <see cref="TargetChannelId"/>
/// MUST be populated — the Teams connector resolves the populated field together
/// with <see cref="TenantId"/> to a <c>ConversationReference</c> via
/// <c>IConversationReferenceStore</c>.
/// </remarks>
public sealed record AgentQuestion : IValidatableObject
{
    /// <summary>Stable id assigned at creation; primary key in the SQL store.</summary>
    [Required(AllowEmptyStrings = false)]
    public string QuestionId { get; init; } = string.Empty;

    /// <summary>Id of the agent that raised the question.</summary>
    [Required(AllowEmptyStrings = false)]
    public string AgentId { get; init; } = string.Empty;

    /// <summary>Id of the task that triggered the question.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TaskId { get; init; } = string.Empty;

    /// <summary>
    /// Entra tenant id of the target user or channel. Required for every proactive
    /// delivery lookup since <c>IConversationReferenceStore</c> keys on
    /// <c>(InternalUserId, TenantId)</c> or <c>(ChannelId, TenantId)</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Internal user id of the recipient; <c>null</c> for channel-scoped questions.</summary>
    public string? TargetUserId { get; init; }

    /// <summary>Teams channel id for channel-scoped questions; <c>null</c> for user-scoped.</summary>
    public string? TargetChannelId { get; init; }

    /// <summary>Card title.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Title { get; init; } = string.Empty;

    /// <summary>Card body.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Body { get; init; } = string.Empty;

    /// <summary>Severity — see <see cref="MessageSeverities"/>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Severity { get; init; } = MessageSeverities.Info;

    /// <summary>Allowed actions rendered as Adaptive Card buttons.</summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<HumanAction> AllowedActions { get; init; } = Array.Empty<HumanAction>();

    /// <summary>UTC timestamp after which <c>QuestionExpiryProcessor</c> moves Status -> Expired.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Teams conversation id the card was delivered to. <c>null</c> on creation;
    /// populated at card-send time. Used by
    /// <c>IAgentQuestionStore.GetOpenByConversationAsync</c> to resolve bare
    /// <c>approve</c> / <c>reject</c> text commands per architecture §2.5.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>Correlation id stitching the question to all related events.</summary>
    [Required(AllowEmptyStrings = false)]
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>
    /// UTC timestamp of creation; set by <c>IAgentQuestionStore.SaveAsync</c> on first
    /// save. Used for ordering in <c>GetOpenByConversationAsync</c> and batch scans in
    /// <c>GetOpenExpiredAsync</c>.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Lifecycle status — see <see cref="AgentQuestionStatuses"/>. Defaults to
    /// <see cref="AgentQuestionStatuses.Open"/>. Transitions are atomic via
    /// <c>IAgentQuestionStore.TryUpdateStatusAsync</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Status { get; init; } = AgentQuestionStatuses.Open;

    /// <summary>
    /// Domain-rule validation: exactly one of <see cref="TargetUserId"/> or
    /// <see cref="TargetChannelId"/> must be populated, and Severity / Status must be
    /// from the canonical vocabularies.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasUser = !string.IsNullOrWhiteSpace(TargetUserId);
        var hasChannel = !string.IsNullOrWhiteSpace(TargetChannelId);
        if (hasUser == hasChannel)
        {
            yield return new ValidationResult(
                "Exactly one of TargetUserId or TargetChannelId must be non-null and non-empty.",
                new[] { nameof(TargetUserId), nameof(TargetChannelId) });
        }

        if (!string.IsNullOrWhiteSpace(Severity)
            && Severity is not (MessageSeverities.Info
                or MessageSeverities.Warning
                or MessageSeverities.Error
                or MessageSeverities.Critical))
        {
            yield return new ValidationResult(
                $"Severity '{Severity}' is not a recognized MessageSeverities value.",
                new[] { nameof(Severity) });
        }

        if (!string.IsNullOrWhiteSpace(Status)
            && Status is not (AgentQuestionStatuses.Open
                or AgentQuestionStatuses.Resolved
                or AgentQuestionStatuses.Expired))
        {
            yield return new ValidationResult(
                $"Status '{Status}' is not a recognized AgentQuestionStatuses value.",
                new[] { nameof(Status) });
        }
    }

    /// <summary>
    /// Convenience helper that runs both <see cref="DataAnnotations"/> attribute
    /// validation and the <see cref="IValidatableObject"/> rules above. Returns an
    /// empty collection when the question is valid.
    /// </summary>
    public IReadOnlyList<ValidationResult> ValidateAll()
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(this, new ValidationContext(this), results, validateAllProperties: true);
        return results;
    }
}
