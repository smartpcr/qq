namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Blocking question raised by an agent that requires a human response via Adaptive Card or
/// equivalent. Aligned with architecture.md §3.1 AgentQuestion field table and
/// e2e-scenarios.md §Proactive Blocking Question. Validation of the semantic invariants
/// (allowed severity / status vocabulary and the <see cref="TargetUserId"/> XOR
/// <see cref="TargetChannelId"/> constraint) is performed by <see cref="Validate"/>.
/// </summary>
public sealed record AgentQuestion
{
    private readonly IReadOnlyList<HumanAction> _allowedActions = Array.Empty<HumanAction>();

    /// <summary>Unique question identifier.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Identity of the agent that raised the question.</summary>
    public required string AgentId { get; init; }

    /// <summary>Identifier of the associated task or work item.</summary>
    public required string TaskId { get; init; }

    /// <summary>
    /// Entra ID tenant of the target user or channel. Required for all proactive delivery
    /// lookups since <c>IConversationReferenceStore</c> keys on
    /// <c>(InternalUserId, TenantId)</c> or <c>(ChannelId, TenantId)</c>. Populated by the
    /// orchestrator from the task's tenant context when creating the question.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Internal user ID (NOT the AAD object ID) of the intended recipient for proactive
    /// delivery. Null when the question is channel-scoped — in which case
    /// <see cref="TargetChannelId"/> must be set. Exactly one of <see cref="TargetUserId"/>
    /// or <see cref="TargetChannelId"/> must be non-null; this is enforced by
    /// <see cref="Validate"/>.
    /// </summary>
    public string? TargetUserId { get; init; }

    /// <summary>
    /// Teams channel ID for channel-scoped questions. Mutually exclusive with
    /// <see cref="TargetUserId"/>.
    /// </summary>
    public string? TargetChannelId { get; init; }

    /// <summary>Short title for the card header.</summary>
    public required string Title { get; init; }

    /// <summary>Detailed question body.</summary>
    public required string Body { get; init; }

    /// <summary>One of <see cref="MessageSeverities.All"/>.</summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Buttons the human can press. A defensive copy is taken at init time so subsequent
    /// caller mutation of the source collection cannot tamper with the question's allowed
    /// actions.
    /// </summary>
    public IReadOnlyList<HumanAction> AllowedActions
    {
        get => _allowedActions;
        init => _allowedActions = value is null
            ? Array.Empty<HumanAction>()
            : value.ToArray();
    }

    /// <summary>UTC expiration deadline. The expiry worker transitions the question to
    /// <see cref="AgentQuestionStatuses.Expired"/> after this instant.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The conversation ID where the question was delivered. Null on creation; populated at
    /// card-send time by the Teams connector with the conversation ID from the active turn
    /// context.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>End-to-end trace ID.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>UTC creation timestamp. Defaults to <see cref="DateTimeOffset.MinValue"/>
    /// until <c>IAgentQuestionStore.SaveAsync</c> stamps the field on first save.</summary>
    public DateTimeOffset CreatedAt { get; init; } = default;

    /// <summary>Lifecycle state — one of <see cref="AgentQuestionStatuses.All"/>. Defaults to
    /// <see cref="AgentQuestionStatuses.Open"/> on creation.</summary>
    public string Status { get; init; } = AgentQuestionStatuses.Open;

    /// <summary>
    /// Returns a list of validation errors for the current instance. An empty list means the
    /// question is valid. Checks all required string members for non-empty values, validates
    /// the allowed severity / status vocabulary, ensures <see cref="AllowedActions"/> is
    /// non-empty, and enforces the <see cref="TargetUserId"/> XOR <see cref="TargetChannelId"/>
    /// invariant.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(QuestionId))
        {
            errors.Add($"{nameof(QuestionId)} is required.");
        }

        if (string.IsNullOrWhiteSpace(AgentId))
        {
            errors.Add($"{nameof(AgentId)} is required.");
        }

        if (string.IsNullOrWhiteSpace(TaskId))
        {
            errors.Add($"{nameof(TaskId)} is required.");
        }

        if (string.IsNullOrWhiteSpace(TenantId))
        {
            errors.Add($"{nameof(TenantId)} is required.");
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            errors.Add($"{nameof(Title)} is required.");
        }

        if (string.IsNullOrWhiteSpace(Body))
        {
            errors.Add($"{nameof(Body)} is required.");
        }

        if (string.IsNullOrWhiteSpace(CorrelationId))
        {
            errors.Add($"{nameof(CorrelationId)} is required.");
        }

        if (!MessageSeverities.IsValid(Severity))
        {
            errors.Add(
                $"{nameof(Severity)} '{Severity}' is not one of [{string.Join(", ", MessageSeverities.All)}].");
        }

        if (!AgentQuestionStatuses.IsValid(Status))
        {
            errors.Add(
                $"{nameof(Status)} '{Status}' is not one of [{string.Join(", ", AgentQuestionStatuses.All)}].");
        }

        if (AllowedActions.Count == 0)
        {
            errors.Add($"{nameof(AllowedActions)} must contain at least one action.");
        }

        var hasUser = !string.IsNullOrWhiteSpace(TargetUserId);
        var hasChannel = !string.IsNullOrWhiteSpace(TargetChannelId);
        if (hasUser == hasChannel)
        {
            errors.Add(
                hasUser
                    ? $"Exactly one of {nameof(TargetUserId)} or {nameof(TargetChannelId)} must be set; both were provided."
                    : $"Exactly one of {nameof(TargetUserId)} or {nameof(TargetChannelId)} must be set; neither was provided.");
        }

        if (ExpiresAt == default)
        {
            errors.Add($"{nameof(ExpiresAt)} must be set to a non-default UTC instant.");
        }

        return errors;
    }
}
