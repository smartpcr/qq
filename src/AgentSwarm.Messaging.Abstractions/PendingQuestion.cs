namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Snapshot of a tracked agent question, returned by
/// <see cref="IPendingQuestionStore.GetAsync"/> /
/// <see cref="IPendingQuestionStore.GetExpiredAsync"/>. Carries the wrapped
/// <see cref="AgentQuestion"/>, the platform-native routing identifiers (see
/// architecture.md Section 4.7), and the resolution bookkeeping populated as
/// the operator engages with the message.
/// </summary>
/// <param name="QuestionId">
/// Stable question identifier (matches <see cref="AgentQuestion.QuestionId"/>).
/// </param>
/// <param name="Question">The original agent question payload.</param>
/// <param name="ChannelId">
/// Connector-native channel identifier the question was posted in (Discord
/// channel snowflake cast to <see cref="long"/>).
/// </param>
/// <param name="PlatformMessageId">
/// Connector-native id of the posted message (Discord message snowflake cast
/// to <see cref="long"/>).
/// </param>
/// <param name="ThreadId">
/// Optional connector-native thread identifier (Discord thread snowflake) when
/// the question was posted into a thread.
/// </param>
/// <param name="DefaultActionId">
/// Cached <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/> at post
/// time, used by the connector for highlight rendering and as the implicit
/// answer when the question times out without a response.
/// </param>
/// <param name="DefaultActionValue">
/// Resolved <see cref="HumanAction.Value"/> for <see cref="DefaultActionId"/>
/// (cached at post time so timeout handling does not need to re-traverse the
/// allowed-actions list).
/// </param>
/// <param name="ExpiresAt">Deadline after which the question is considered timed out.</param>
/// <param name="Status">Lifecycle state. See <see cref="PendingQuestionStatus"/>.</param>
/// <param name="SelectedActionId">
/// Action chosen by the operator. <see langword="null"/> until the operator
/// acts.
/// </param>
/// <param name="SelectedActionValue">
/// Resolved value for the selected action. <see langword="null"/> until the
/// operator acts.
/// </param>
/// <param name="RespondentUserId">
/// Connector-native user id of the responding operator (Discord user snowflake
/// cast to <see cref="long"/>). <see langword="null"/> until the operator acts.
/// </param>
/// <param name="StoredAt">When the question was first persisted.</param>
/// <param name="CorrelationId">
/// End-to-end trace identifier propagated from the originating
/// <see cref="AgentQuestion.CorrelationId"/>.
/// </param>
public sealed record PendingQuestion(
    string QuestionId,
    AgentQuestion Question,
    long ChannelId,
    long PlatformMessageId,
    long? ThreadId,
    string? DefaultActionId,
    string? DefaultActionValue,
    DateTimeOffset ExpiresAt,
    PendingQuestionStatus Status,
    string? SelectedActionId,
    string? SelectedActionValue,
    long? RespondentUserId,
    DateTimeOffset StoredAt,
    string CorrelationId);
