namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Thrown by <see cref="TeamsProactiveNotifier"/> when no
/// <see cref="TeamsConversationReference"/> exists in <see cref="IConversationReferenceStore"/>
/// for the supplied target (either a personal-scope user identified by
/// <see cref="TenantId"/> + <see cref="InternalUserId"/> or a channel-scope target
/// identified by <see cref="TenantId"/> + <see cref="ChannelId"/>). Aligned with the
/// "reference not found" test scenario in <c>implementation-plan.md</c> §4.2.
/// </summary>
/// <remarks>
/// <para>
/// Callers (the orchestrator and the Stage 6 outbox engine) catch this exception and
/// route the message to the dead-letter queue or schedule a retry after the user
/// (re-)installs the Teams app. The exception is intentionally distinct from
/// <see cref="InvalidOperationException"/> so the outbox can recognize the
/// "never-installed / reference-evicted" case without inspecting the message text.
/// </para>
/// <para>
/// The exception carries enough context (<see cref="TenantId"/>, the populated
/// <see cref="InternalUserId"/> or <see cref="ChannelId"/>, and the optional
/// <see cref="QuestionId"/>) to drive a structured audit-log entry without re-parsing
/// the message.
/// </para>
/// </remarks>
public sealed class ConversationReferenceNotFoundException : Exception
{
    /// <summary>Entra ID tenant of the target.</summary>
    public string TenantId { get; }

    /// <summary>
    /// Internal user ID of the target — non-null when the failed lookup was for a
    /// personal-scope user. Mutually exclusive with <see cref="ChannelId"/>.
    /// </summary>
    public string? InternalUserId { get; }

    /// <summary>
    /// Teams channel ID of the target — non-null when the failed lookup was for a
    /// channel-scope target. Mutually exclusive with <see cref="InternalUserId"/>.
    /// </summary>
    public string? ChannelId { get; }

    /// <summary>
    /// Originating <c>AgentQuestion.QuestionId</c> when the lookup was triggered by a
    /// question-send; null when the lookup was triggered by a bare message send.
    /// </summary>
    public string? QuestionId { get; }

    private ConversationReferenceNotFoundException(
        string message,
        string tenantId,
        string? internalUserId,
        string? channelId,
        string? questionId)
        : base(message)
    {
        TenantId = tenantId;
        InternalUserId = internalUserId;
        ChannelId = channelId;
        QuestionId = questionId;
    }

    /// <summary>
    /// Factory for the "user-scope reference not found" case used by
    /// <see cref="TeamsProactiveNotifier.SendProactiveAsync"/> and
    /// <see cref="TeamsProactiveNotifier.SendProactiveQuestionAsync"/>.
    /// </summary>
    /// <param name="tenantId">The tenant the lookup was performed in.</param>
    /// <param name="internalUserId">The internal user ID the lookup was performed for.</param>
    /// <param name="questionId">Optional originating question ID.</param>
    public static ConversationReferenceNotFoundException ForUser(
        string tenantId,
        string internalUserId,
        string? questionId = null)
    {
        var msg =
            $"No active TeamsConversationReference found for tenant '{tenantId}', " +
            $"internalUserId '{internalUserId}'" +
            (questionId is null ? "." : $", question '{questionId}'.") +
            " The Teams app must be installed for the target user before proactive delivery can succeed.";
        return new ConversationReferenceNotFoundException(msg, tenantId, internalUserId, channelId: null, questionId);
    }

    /// <summary>
    /// Factory for the "channel-scope reference not found" case used by
    /// <see cref="TeamsProactiveNotifier.SendToChannelAsync"/> and
    /// <see cref="TeamsProactiveNotifier.SendQuestionToChannelAsync"/>.
    /// </summary>
    /// <param name="tenantId">The tenant the lookup was performed in.</param>
    /// <param name="channelId">The Teams channel ID the lookup was performed for.</param>
    /// <param name="questionId">Optional originating question ID.</param>
    public static ConversationReferenceNotFoundException ForChannel(
        string tenantId,
        string channelId,
        string? questionId = null)
    {
        var msg =
            $"No active TeamsConversationReference found for tenant '{tenantId}', " +
            $"channelId '{channelId}'" +
            (questionId is null ? "." : $", question '{questionId}'.") +
            " The Teams app must be installed in the target channel before proactive delivery can succeed.";
        return new ConversationReferenceNotFoundException(msg, tenantId, internalUserId: null, channelId, questionId);
    }
}
