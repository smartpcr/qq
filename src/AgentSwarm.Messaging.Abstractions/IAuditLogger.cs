namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Cross-platform contract for the messenger audit log. Captures a per-event
/// row for every operator-visible action (inbound commands, outbound messages,
/// authorization decisions, human responses) so compliance and post-mortem
/// review can reconstruct who did what, when, and on which platform. See
/// architecture.md Section 4.10 and FR-006 in
/// <c>.forge-attachments/agent_swarm_messenger_user_stories.md</c>.
/// </summary>
/// <remarks>
/// The two methods exist because human decisions carry strongly-typed extra
/// fields (question id, selected action, optional rationale) that compliance
/// readers query directly. Implementations may persist both into a single
/// table provided the additional <see cref="HumanResponseAuditEntry"/> columns
/// are queryable.
/// </remarks>
public interface IAuditLogger
{
    /// <summary>
    /// Persists a generic audit row.
    /// </summary>
    /// <param name="entry">The audit entry to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LogAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>
    /// Persists a human-decision audit row (button click, select-menu choice,
    /// modal submission resolving an <see cref="AgentQuestion"/>).
    /// </summary>
    /// <param name="entry">The decision entry to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct);
}
