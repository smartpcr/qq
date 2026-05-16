namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Platform-agnostic contract for delivering agent-originated <see cref="MessengerMessage"/>
/// notifications and <see cref="AgentQuestion"/> blocking questions to a specific user or
/// channel <i>proactively</i> — that is, outside of an active inbound turn context. Concrete
/// implementations resolve a previously-captured platform conversation reference from a
/// natural key (<c>(TenantId, InternalUserId)</c> for user-targeted sends or
/// <c>(TenantId, ChannelId)</c> for channel-targeted sends) and dispatch through the
/// underlying messenger SDK's continuation API
/// (Bot Framework's <c>ContinueConversationAsync</c>, Slack's <c>chat.postMessage</c>, etc.).
/// Aligned with <c>implementation-plan.md</c> §4.2 and <c>architecture.md</c> §4.7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why all methods require a <c>tenantId</c> parameter.</b> Conversation reference
/// stores are keyed by <c>(InternalUserId, TenantId)</c> for user-targeted sends and
/// <c>(ChannelId, TenantId)</c> for channel-targeted sends — the tenant scope is part of
/// the natural primary key. For user-targeted methods the value is supplied from
/// <see cref="AgentQuestion.TenantId"/> (populated by the orchestrator); for
/// channel-targeted methods it is passed directly by the caller.
/// </para>
/// <para>
/// <b>Routing helper.</b> The <see cref="NotifyQuestionAsync"/> default interface method
/// dispatches an <see cref="AgentQuestion"/> to the appropriate per-target method based on
/// which of <see cref="AgentQuestion.TargetUserId"/> or
/// <see cref="AgentQuestion.TargetChannelId"/> is populated. Implementations rarely need to
/// override it — the dispatch is the same for every messenger because the question
/// validation invariant guarantees exactly one target field is set (see
/// <see cref="AgentQuestion.Validate"/>).
/// </para>
/// <para>
/// <b>Failure mode.</b> When no conversation reference exists for the supplied target,
/// implementations throw a platform-specific exception (e.g.,
/// <c>ConversationReferenceNotFoundException</c> for Teams). Callers are expected to log
/// the failure and, when the reliability layer is wired in Phase 6, route the message to
/// the outbox for later retry.
/// </para>
/// </remarks>
public interface IProactiveNotifier
{
    /// <summary>
    /// Send <paramref name="message"/> proactively to the personal chat of the user
    /// identified by <paramref name="userId"/> in <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant (or equivalent platform tenant key).</param>
    /// <param name="userId">Internal user ID (NOT the AAD object ID).</param>
    /// <param name="message">The platform-agnostic message to deliver.</param>
    /// <param name="ct">Cancellation token observed by the underlying SDK call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> or <paramref name="userId"/> is null/whitespace.</exception>
    Task SendProactiveAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct);

    /// <summary>
    /// Send <paramref name="question"/> proactively to the personal chat of the user
    /// identified by <paramref name="userId"/> in <paramref name="tenantId"/>. The
    /// implementation renders the question as the platform-native interactive card
    /// (Teams Adaptive Card, Slack Block Kit, etc.) and persists any platform-specific
    /// follow-up state (Teams activity ID + conversation reference for later
    /// update/delete; outbox enqueue in Phase 6).
    /// </summary>
    /// <param name="tenantId">Entra ID tenant (or equivalent platform tenant key).</param>
    /// <param name="userId">Internal user ID (NOT the AAD object ID).</param>
    /// <param name="question">The blocking question to deliver.</param>
    /// <param name="ct">Cancellation token observed by the underlying SDK call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="question"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> or <paramref name="userId"/> is null/whitespace.</exception>
    Task SendProactiveQuestionAsync(string tenantId, string userId, AgentQuestion question, CancellationToken ct);

    /// <summary>
    /// Send <paramref name="message"/> proactively to a team channel identified by
    /// <paramref name="channelId"/> in <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant (or equivalent platform tenant key).</param>
    /// <param name="channelId">Channel identifier (Teams channel ID, Slack channel ID, etc.).</param>
    /// <param name="message">The platform-agnostic message to deliver.</param>
    /// <param name="ct">Cancellation token observed by the underlying SDK call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> or <paramref name="channelId"/> is null/whitespace.</exception>
    Task SendToChannelAsync(string tenantId, string channelId, MessengerMessage message, CancellationToken ct);

    /// <summary>
    /// Send <paramref name="question"/> proactively to a team channel identified by
    /// <paramref name="channelId"/> in <paramref name="tenantId"/>. The implementation
    /// renders the question as the platform-native interactive card and persists any
    /// platform-specific follow-up state.
    /// </summary>
    /// <param name="tenantId">Entra ID tenant (or equivalent platform tenant key).</param>
    /// <param name="channelId">Channel identifier (Teams channel ID, Slack channel ID, etc.).</param>
    /// <param name="question">The blocking question to deliver.</param>
    /// <param name="ct">Cancellation token observed by the underlying SDK call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="question"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> or <paramref name="channelId"/> is null/whitespace.</exception>
    Task SendQuestionToChannelAsync(string tenantId, string channelId, AgentQuestion question, CancellationToken ct);

    /// <summary>
    /// Routing helper: dispatch <paramref name="question"/> to the per-target method that
    /// matches its populated target field. When
    /// <see cref="AgentQuestion.TargetUserId"/> is non-null the call is delegated to
    /// <see cref="SendProactiveQuestionAsync"/>; when
    /// <see cref="AgentQuestion.TargetChannelId"/> is non-null the call is delegated to
    /// <see cref="SendQuestionToChannelAsync"/>. The question is validated first via
    /// <see cref="AgentQuestion.Validate"/>; an <see cref="InvalidOperationException"/> is
    /// thrown when validation fails (which includes the case where neither or both target
    /// fields are populated).
    /// </summary>
    /// <param name="question">The question whose target fields drive the routing decision.</param>
    /// <param name="ct">Cancellation token observed by the underlying SDK call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="question"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="question"/> fails <see cref="AgentQuestion.Validate"/>.</exception>
    Task NotifyQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);

        var validationErrors = question.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{question.QuestionId}' cannot be routed: {string.Join("; ", validationErrors)}");
        }

        // Validate() already enforces the TargetUserId XOR TargetChannelId invariant, so the
        // two branches below are mutually exclusive and exhaustive.
        if (!string.IsNullOrWhiteSpace(question.TargetUserId))
        {
            return SendProactiveQuestionAsync(question.TenantId, question.TargetUserId!, question, ct);
        }

        return SendQuestionToChannelAsync(question.TenantId, question.TargetChannelId!, question, ct);
    }
}
