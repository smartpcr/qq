namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Manages task-to-operator oversight assignments for the <c>/handoff</c>
/// flow. Defined in <c>AgentSwarm.Messaging.Core</c> so the swarm-event
/// subscription service can resolve the oversighting operator for
/// status-update and alert routing without taking a dependency on
/// <c>AgentSwarm.Messaging.Persistence</c>.
/// </summary>
public interface ITaskOversightRepository
{
    /// <summary>
    /// Returns the current oversight assignment for the supplied task, or
    /// <c>null</c> when no assignment exists (callers fall back to workspace
    /// default routing).
    /// </summary>
    Task<TaskOversight?> GetByTaskIdAsync(string taskId, CancellationToken ct);

    /// <summary>
    /// Idempotently create or update the oversight binding for a task. Used
    /// for both initial assignment and <c>/handoff</c> reassignment.
    /// </summary>
    Task UpsertAsync(TaskOversight oversight, CancellationToken ct);

    /// <summary>
    /// All tasks currently overseen by the supplied operator binding. Used
    /// by <c>/status</c> to render operator-scoped task lists.
    /// </summary>
    Task<IReadOnlyList<TaskOversight>> GetByOperatorAsync(
        Guid operatorBindingId,
        CancellationToken ct);
}
