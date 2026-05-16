using AgentSwarm.Messaging.Core;

namespace AgentSwarm.Messaging.Telegram.Swarm;

/// <summary>
/// Stage 2.7 dev/test stub <see cref="ITaskOversightRepository"/>.
/// <see cref="GetByTaskIdAsync"/> always returns <c>null</c>, which causes
/// the <c>SwarmEventSubscriptionService</c> to fall back to:
/// <list type="bullet">
///   <item><see cref="AgentAlertEvent"/>: workspace-default routing via
///   <see cref="IOperatorRegistry.GetByWorkspaceAsync"/> (architecture.md
///   §5.6).</item>
///   <item><see cref="AgentStatusUpdateEvent"/>: broadcast to every active
///   binding in the tenant via
///   <see cref="IOperatorRegistry.GetByTenantAsync"/>
///   (implementation-plan.md Stage 2.7).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacement contract.</b> Registered via
/// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}"/>
/// in <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>; the
/// Stage 3.2 <c>PersistentTaskOversightRepository</c>
/// (<c>AddSingleton</c>) wins by last-wins semantics.
/// <b>Production-readiness gate (Stage 6.3).</b> Startup health check
/// asserts the resolved <see cref="ITaskOversightRepository"/> is NOT
/// this stub when <c>ASPNETCORE_ENVIRONMENT=Production</c>.
/// </para>
/// <para>
/// <b>Writes are loud no-ops.</b> <see cref="UpsertAsync"/> validates its
/// argument and logs / returns; <see cref="GetByOperatorAsync"/> returns
/// an empty list. The stub deliberately does NOT throw on writes so the
/// <c>/handoff</c> command flow can be exercised end-to-end against the
/// stub in dev hosts, even though the upsert is discarded — the read
/// returning <c>null</c> on the next request makes that visible.
/// </para>
/// </remarks>
public sealed class StubTaskOversightRepository : ITaskOversightRepository
{
    /// <inheritdoc />
    public Task<TaskOversight?> GetByTaskIdAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return Task.FromResult<TaskOversight?>(null);
    }

    /// <inheritdoc />
    public Task UpsertAsync(TaskOversight oversight, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(oversight);
        // Intentionally discarded — see remarks. The persistent
        // replacement (Stage 3.2) durably stores the upsert.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TaskOversight>> GetByOperatorAsync(
        Guid operatorBindingId,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<TaskOversight>>(Array.Empty<TaskOversight>());
    }
}
