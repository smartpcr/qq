using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Single source of truth for the validation / security guards that every
/// <see cref="AgentQuestion"/> send (direct or outbox-backed) MUST run before the
/// payload reaches the network or the durable outbox. Used by
/// <see cref="TeamsProactiveNotifier"/> on the direct path and by
/// <see cref="Outbox.OutboxBackedProactiveNotifier"/> /
/// <see cref="Outbox.OutboxBackedMessengerConnector"/> on the outbox-backed path so
/// the public contract — exception type, parameter name, message shape, and
/// ordering — is identical across both surfaces. Without a shared helper the two
/// paths drifted: a caller could observe one exception type on the direct path and a
/// different type for the same misuse on the outbox-backed path, which silently
/// breaks downstream <c>catch</c> filters and audit-row attribution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical send-time ordering.</b> Both
/// <c>SendProactiveQuestionAsync</c> and <c>SendQuestionToChannelAsync</c> entry
/// points MUST invoke the guards in this exact sequence (after the
/// caller-parameter null / blank checks):
/// <list type="number">
///   <item><description><see cref="EnsureTenantMatchesQuestion"/> — tenant
///     isolation invariant. Throws <see cref="ArgumentException"/> bound to
///     <c>tenantId</c>.</description></item>
///   <item><description><see cref="EnsureScopeUserTargeted"/> /
///     <see cref="EnsureScopeChannelTargeted"/> — scope routing invariant. Throws
///     <see cref="ArgumentException"/> bound to <c>question</c> (scope mismatch)
///     or <c>userId</c> / <c>channelId</c> (identifier mismatch).</description></item>
///   <item><description><see cref="ValidateQuestion"/> — payload-shape invariants
///     (required string members, severity / status vocabulary, exactly-one of
///     <see cref="AgentQuestion.TargetUserId"/> /
///     <see cref="AgentQuestion.TargetChannelId"/>, non-empty
///     <see cref="AgentQuestion.AllowedActions"/>, non-default
///     <see cref="AgentQuestion.ExpiresAt"/>). Throws
///     <see cref="InvalidOperationException"/>.</description></item>
/// </list>
/// The tenant / scope guards intentionally precede payload validation so a caller
/// that passes a mismatched tenant or scope sees an <see cref="ArgumentException"/>
/// (a parameter-shaped failure they can route to a 400-class response) rather than
/// a malformed-payload <see cref="InvalidOperationException"/> that would also fire
/// for the same input.
/// </para>
/// <para>
/// <b>Retry parity.</b> When an existing <see cref="IAgentQuestionStore"/> row is
/// already present at send time, <see cref="EnsureRetryMatchesStoredQuestion"/>
/// MUST run before the new send proceeds so a mutated retry does not ship a card
/// whose payload diverges from the row <c>CardActionHandler.GetByIdAsync</c> loads
/// on the user's approve / reject reply. <c>ConversationId</c>, <c>Status</c>,
/// <c>CreatedAt</c>, and <c>ResolvedAt</c> are store-owned lifecycle fields and
/// are deliberately excluded from the comparison.
/// </para>
/// </remarks>
internal static class TeamsQuestionSendGuards
{
    /// <summary>
    /// Run <see cref="AgentQuestion.Validate"/> and surface any errors as
    /// <see cref="InvalidOperationException"/>. The exception message includes the
    /// <see cref="AgentQuestion.QuestionId"/> and the joined validation error list
    /// so audit logs can attribute the failure to a specific question.
    /// </summary>
    public static void ValidateQuestion(AgentQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var errors = question.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{question.QuestionId}' is invalid: {string.Join("; ", errors)}");
        }
    }

    /// <summary>
    /// Tenant-isolation guard — the caller-supplied <paramref name="tenantId"/> must
    /// equal <see cref="AgentQuestion.TenantId"/>. String equality is case-sensitive
    /// because AAD tenant GUIDs are normalised at the issuer and Azure recommends
    /// preserving the exact casing of the <c>tid</c> claim. Throws
    /// <see cref="ArgumentException"/> bound to <c>tenantId</c> so direct callers and
    /// DI alike see a parameter-shaped failure they can attribute.
    /// </summary>
    public static void EnsureTenantMatchesQuestion(string tenantId, AgentQuestion question)
    {
        if (!string.Equals(tenantId, question.TenantId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"tenantId '{tenantId}' does not match AgentQuestion '{question.QuestionId}' " +
                $"tenant '{question.TenantId}'. Refusing to send a question through a tenant " +
                $"different from its own routing metadata — this is a tenant-isolation invariant.",
                nameof(tenantId));
        }
    }

    /// <summary>
    /// User-scope guard for the personal-chat entry points. Two failure modes:
    /// (1) the question is channel-scoped
    /// (<see cref="AgentQuestion.TargetChannelId"/> is non-null) — delivering it into
    /// a personal chat would mis-route the approval ask and leak channel context
    /// into a 1:1 thread; (2) the supplied <paramref name="userId"/> does not match
    /// <see cref="AgentQuestion.TargetUserId"/> — delivering it to a different user
    /// would route the approval ask to the wrong person. Both throw
    /// <see cref="ArgumentException"/> — bound to <c>question</c> for scope mismatches
    /// and to <c>userId</c> for identifier mismatches.
    /// </summary>
    public static void EnsureScopeUserTargeted(string userId, AgentQuestion question)
    {
        if (question.TargetChannelId is not null)
        {
            throw new ArgumentException(
                $"AgentQuestion '{question.QuestionId}' is channel-scoped " +
                $"(TargetChannelId='{question.TargetChannelId}') but SendProactiveQuestionAsync " +
                $"is the user-scope entry point. Route channel-scoped questions through " +
                $"SendQuestionToChannelAsync or the NotifyQuestionAsync dispatcher.",
                nameof(question));
        }

        if (!string.Equals(userId, question.TargetUserId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"userId '{userId}' does not match AgentQuestion '{question.QuestionId}' " +
                $"TargetUserId '{question.TargetUserId}'. Refusing to deliver an approval ask " +
                $"to a user other than the one named on the question.",
                nameof(userId));
        }
    }

    /// <summary>
    /// Channel-scope guard for the channel entry points. Mirrors
    /// <see cref="EnsureScopeUserTargeted"/>: rejects user-scoped questions
    /// (<see cref="AgentQuestion.TargetUserId"/> set) and channel-id mismatches.
    /// </summary>
    public static void EnsureScopeChannelTargeted(string channelId, AgentQuestion question)
    {
        if (question.TargetUserId is not null)
        {
            throw new ArgumentException(
                $"AgentQuestion '{question.QuestionId}' is user-scoped " +
                $"(TargetUserId='{question.TargetUserId}') but SendQuestionToChannelAsync " +
                $"is the channel-scope entry point. Route user-scoped questions through " +
                $"SendProactiveQuestionAsync or the NotifyQuestionAsync dispatcher.",
                nameof(question));
        }

        if (!string.Equals(channelId, question.TargetChannelId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"channelId '{channelId}' does not match AgentQuestion '{question.QuestionId}' " +
                $"TargetChannelId '{question.TargetChannelId}'. Refusing to deliver an approval " +
                $"ask to a channel other than the one named on the question.",
                nameof(channelId));
        }
    }

    /// <summary>
    /// Retry-drift guard — when a stored <see cref="AgentQuestion"/> row is already
    /// present at send time, every identity / routing / payload field on the incoming
    /// question MUST match the stored row before the new send proceeds. A mismatch
    /// means the orchestrator mutated the question between attempts, which is "card
    /// update" semantics (not retry) and is not supported by the proactive question
    /// pipeline — the card delivered to Teams would drift from the row
    /// <c>CardActionHandler.GetByIdAsync</c> loads when the user replies.
    /// </summary>
    /// <remarks>
    /// Fields compared:
    /// <list type="bullet">
    ///   <item><description>Identity: <c>TenantId</c>, <c>AgentId</c>, <c>TaskId</c>, <c>CorrelationId</c>.</description></item>
    ///   <item><description>Routing: <c>TargetUserId</c>, <c>TargetChannelId</c> (null vs empty normalised).</description></item>
    ///   <item><description>Payload: <c>Title</c>, <c>Body</c>, <c>Severity</c>, <c>ExpiresAt</c>, and each
    ///     <c>AllowedActions</c> entry's <c>ActionId</c> / <c>Label</c> / <c>Value</c> /
    ///     <c>RequiresComment</c>.</description></item>
    /// </list>
    /// <c>QuestionId</c> equality is guaranteed because the lookup was keyed by it.
    /// <c>ConversationId</c>, <c>Status</c>, <c>CreatedAt</c>, and <c>ResolvedAt</c>
    /// are store-owned lifecycle fields and are NOT compared — the sanitised
    /// pre-save deliberately blanks <c>ConversationId</c> and pins
    /// <c>Status = Open</c>, and the dispatcher stamps <c>ConversationId</c> back
    /// only after delivery.
    /// </remarks>
    public static void EnsureRetryMatchesStoredQuestion(AgentQuestion incoming, AgentQuestion stored)
    {
        static string Norm(string? s) => s ?? string.Empty;

        var mismatches = new List<string>();
        if (!string.Equals(incoming.TenantId, stored.TenantId, StringComparison.Ordinal))
        {
            mismatches.Add($"TenantId (incoming='{incoming.TenantId}', stored='{stored.TenantId}')");
        }
        if (!string.Equals(incoming.AgentId, stored.AgentId, StringComparison.Ordinal))
        {
            mismatches.Add($"AgentId (incoming='{incoming.AgentId}', stored='{stored.AgentId}')");
        }
        if (!string.Equals(incoming.TaskId, stored.TaskId, StringComparison.Ordinal))
        {
            mismatches.Add($"TaskId (incoming='{incoming.TaskId}', stored='{stored.TaskId}')");
        }
        if (!string.Equals(incoming.CorrelationId, stored.CorrelationId, StringComparison.Ordinal))
        {
            mismatches.Add($"CorrelationId (incoming='{incoming.CorrelationId}', stored='{stored.CorrelationId}')");
        }
        if (!string.Equals(Norm(incoming.TargetUserId), Norm(stored.TargetUserId), StringComparison.Ordinal))
        {
            mismatches.Add($"TargetUserId (incoming='{incoming.TargetUserId}', stored='{stored.TargetUserId}')");
        }
        if (!string.Equals(Norm(incoming.TargetChannelId), Norm(stored.TargetChannelId), StringComparison.Ordinal))
        {
            mismatches.Add($"TargetChannelId (incoming='{incoming.TargetChannelId}', stored='{stored.TargetChannelId}')");
        }
        if (!string.Equals(incoming.Title, stored.Title, StringComparison.Ordinal))
        {
            mismatches.Add("Title");
        }
        if (!string.Equals(incoming.Body, stored.Body, StringComparison.Ordinal))
        {
            mismatches.Add("Body");
        }
        if (!string.Equals(incoming.Severity, stored.Severity, StringComparison.Ordinal))
        {
            mismatches.Add($"Severity (incoming='{incoming.Severity}', stored='{stored.Severity}')");
        }
        if (incoming.ExpiresAt != stored.ExpiresAt)
        {
            mismatches.Add($"ExpiresAt (incoming='{incoming.ExpiresAt:o}', stored='{stored.ExpiresAt:o}')");
        }
        if (incoming.AllowedActions.Count != stored.AllowedActions.Count)
        {
            mismatches.Add($"AllowedActions.Count (incoming={incoming.AllowedActions.Count}, stored={stored.AllowedActions.Count})");
        }
        else
        {
            for (var i = 0; i < incoming.AllowedActions.Count; i++)
            {
                var a = incoming.AllowedActions[i];
                var b = stored.AllowedActions[i];
                if (!string.Equals(a.ActionId, b.ActionId, StringComparison.Ordinal)
                    || !string.Equals(a.Label, b.Label, StringComparison.Ordinal)
                    || !string.Equals(a.Value, b.Value, StringComparison.Ordinal)
                    || a.RequiresComment != b.RequiresComment)
                {
                    mismatches.Add($"AllowedActions[{i}]");
                }
            }
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{incoming.QuestionId}' was already persisted with different metadata than the incoming retry; " +
                $"refusing to deliver a card whose payload would diverge from the stored row that CardActionHandler will load on reply. " +
                $"Mismatched fields: {string.Join(", ", mismatches)}. " +
                $"Mutating an in-flight question is not supported — preserve the original payload on retry or assign a new QuestionId.");
        }
    }
}
