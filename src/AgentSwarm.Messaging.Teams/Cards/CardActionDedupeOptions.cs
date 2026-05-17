namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Options controlling the in-memory processed-action set introduced in Stage 6.2 of
/// <c>implementation-plan.md</c> (Duplicate Suppression and Idempotency) and described in
/// <c>architecture.md</c> §2.6 layer 2.
/// </summary>
/// <remarks>
/// <para>
/// The set is a domain-level <i>fast-path</i> idempotency guard for Adaptive Card actions
/// keyed on <c>(QuestionId, UserId)</c>. It catches user-initiated double-taps without
/// touching any store. The authoritative cross-pod / cross-restart guarantee is provided
/// by <see cref="AgentSwarm.Messaging.Abstractions.IAgentQuestionStore.TryUpdateStatusAsync"/>
/// (the durable <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion.Status"/>
/// compare-and-set introduced in Stage 3.3).
/// </para>
/// <para>
/// Defaults match the Stage 6.2 brief:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="EntryLifetime"/> defaults to <c>24 hours</c>.</description></item>
/// <item><description><see cref="EvictionInterval"/> defaults to <c>5 minutes</c>.</description></item>
/// </list>
/// </remarks>
public sealed class CardActionDedupeOptions
{
    /// <summary>
    /// Time a <c>(QuestionId, UserId)</c> entry remains addressable in the processed-action
    /// set before <see cref="ProcessedCardActionEvictionService"/> purges it. Must be a
    /// strictly positive <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Background eviction cadence — how often
    /// <see cref="ProcessedCardActionEvictionService"/> wakes up and prunes entries whose
    /// age exceeds <see cref="EntryLifetime"/>. Must be a strictly positive
    /// <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromMinutes(5);
}
