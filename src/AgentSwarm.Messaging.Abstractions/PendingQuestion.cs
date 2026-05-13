namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Lifecycle status of a <see cref="PendingQuestion"/>.
/// </summary>
public enum PendingQuestionStatus
{
    /// <summary>Question has been sent; no action has been recorded yet.</summary>
    Pending,

    /// <summary>
    /// The operator tapped a <see cref="HumanAction"/> whose
    /// <see cref="HumanAction.RequiresComment"/> is <c>true</c>; the store is
    /// waiting for a follow-up text reply to complete the decision.
    /// </summary>
    AwaitingComment,

    /// <summary>The decision has been fully recorded and emitted.</summary>
    Answered,

    /// <summary>The question expired before any answer was recorded.</summary>
    TimedOut
}

/// <summary>
/// Abstraction-level DTO returned by <see cref="IPendingQuestionStore"/>.
/// The concrete persistence entity <c>PendingQuestionRecord</c> (Stage 3.5)
/// maps to/from this record. Fields are denormalized at persistence time so
/// the connector never needs to consult <c>IDistributedCache</c> for
/// callback resolution.
/// </summary>
public sealed record PendingQuestion
{
    public required string QuestionId { get; init; }

    public required string AgentId { get; init; }

    /// <summary>
    /// Originating task identifier; preserved from the source
    /// <see cref="AgentQuestion.TaskId"/> so timeout / response handlers can
    /// route the resulting <see cref="HumanDecisionEvent"/> back to the
    /// correct work item without consulting an external source. Required by
    /// e2e-scenarios.md "Question includes context, severity, timeout, and
    /// proposed default" (line 100).
    /// </summary>
    public required string TaskId { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    /// <summary>
    /// Severity preserved from the source <see cref="AgentQuestion.Severity"/>.
    /// Drives the severity badge rendered on the original Telegram message
    /// and the priority lane the timeout/comment response is published on.
    /// Required by e2e-scenarios.md "Question includes context, severity,
    /// timeout, and proposed default" (line 103).
    /// </summary>
    public required MessageSeverity Severity { get; init; }

    public required IReadOnlyList<HumanAction> AllowedActions { get; init; }

    /// <summary>
    /// <see cref="HumanAction.ActionId"/> of the proposed default carried from
    /// the envelope sidecar; <c>null</c> when the envelope did not propose
    /// one (in which case timeout publishes <c>ActionValue = "__timeout__"</c>).
    /// </summary>
    public string? DefaultActionId { get; init; }

    /// <summary>
    /// The <see cref="HumanAction.Value"/> of the proposed default,
    /// denormalized at <see cref="IPendingQuestionStore.StoreAsync"/> time
    /// so <c>QuestionTimeoutService</c> can emit a
    /// <see cref="HumanDecisionEvent"/> without a cache lookup.
    /// </summary>
    public string? DefaultActionValue { get; init; }

    public required long TelegramChatId { get; init; }

    public required long TelegramMessageId { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    private readonly string _correlationId = null!;

    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }

    public required PendingQuestionStatus Status { get; init; }

    /// <summary>Set when the operator tapped an inline button.</summary>
    public string? SelectedActionId { get; init; }

    /// <summary>
    /// The <see cref="HumanAction.Value"/> matching
    /// <see cref="SelectedActionId"/>, resolved at button-tap time and
    /// persisted so the text-reply handler can publish from durable storage.
    /// </summary>
    public string? SelectedActionValue { get; init; }

    /// <summary>
    /// Telegram user ID of the operator who tapped the button; used with
    /// <see cref="TelegramChatId"/> and <see cref="PendingQuestionStatus.AwaitingComment"/>
    /// to correlate follow-up text replies.
    /// </summary>
    public long? RespondentUserId { get; init; }

    /// <summary>
    /// Wall-clock timestamp at which the record was persisted after a
    /// successful Telegram send. Used for deterministic oldest-first tie
    /// breaking when multiple <see cref="PendingQuestionStatus.AwaitingComment"/>
    /// questions exist for the same operator.
    /// </summary>
    public required DateTimeOffset StoredAt { get; init; }
}
