using System.Runtime.CompilerServices;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Telegram.Swarm;

/// <summary>
/// Stage 2.7 dev/test stub <see cref="ISwarmCommandBus"/>. The default
/// shape lets the <c>SwarmEventSubscriptionService</c> wire up cleanly in
/// hosts that have not yet bound a real swarm transport: outbound
/// publishes are logged at <see cref="LogLevel.Debug"/> and discarded;
/// queries return empty/minimal projections; the inbound
/// <see cref="SubscribeAsync"/> stream completes immediately with no
/// events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacement contract (architecture.md §4.6).</b> Registered via
/// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}"/>
/// in <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>; the
/// concrete swarm-transport adapter (out of scope for this story)
/// registers via <c>AddSingleton</c> and wins by last-wins semantics.
/// <b>Production-readiness gate (Stage 6.3).</b> Startup health check
/// asserts the resolved <see cref="ISwarmCommandBus"/> is NOT this stub
/// when <c>ASPNETCORE_ENVIRONMENT=Production</c>.
/// </para>
/// <para>
/// <b>SubscribeAsync semantics.</b> The stub yields no events but the
/// returned <see cref="IAsyncEnumerable{T}"/> is well-formed (terminates
/// cleanly when enumerated rather than blocking forever) so the
/// <c>SwarmEventSubscriptionService</c> can finish its
/// <c>await foreach</c> in dev hosts without holding the worker open.
/// </para>
/// <para>
/// <b>Why log at Debug.</b> Commands published into a stub bus are
/// definitionally non-actionable; logging them at <see cref="LogLevel.Information"/>
/// or above would clutter production logs once a real bus is registered
/// alongside the stub (e.g. during a staged rollout). Debug keeps the
/// trail visible without flooding the default log volume.
/// </para>
/// </remarks>
public sealed class StubSwarmCommandBus : ISwarmCommandBus
{
    private readonly ILogger<StubSwarmCommandBus> _logger;

    public StubSwarmCommandBus(ILogger<StubSwarmCommandBus> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task PublishCommandAsync(SwarmCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        _logger.LogDebug(
            "StubSwarmCommandBus discarding SwarmCommand. CommandType={CommandType} TaskId={TaskId} OperatorId={OperatorId} CorrelationId={CorrelationId}",
            command.CommandType,
            command.TaskId,
            command.OperatorId,
            command.CorrelationId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PublishHumanDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _logger.LogDebug(
            "StubSwarmCommandBus discarding HumanDecisionEvent. QuestionId={QuestionId} ActionValue={ActionValue} CorrelationId={CorrelationId}",
            decision.QuestionId,
            decision.ActionValue,
            decision.CorrelationId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SwarmStatusSummary> QueryStatusAsync(SwarmStatusQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Echo the workspace and mark the state as "stub" so callers can
        // tell at a glance that no real orchestrator answered.
        return Task.FromResult(new SwarmStatusSummary
        {
            WorkspaceId = query.WorkspaceId,
            State = "stub",
            TaskId = query.TaskId,
            ActiveAgentCount = 0,
            PendingTaskCount = 0,
            DisplayText = "Swarm command bus is unconfigured (StubSwarmCommandBus).",
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult<IReadOnlyList<AgentInfo>>(Array.Empty<AgentInfo>());
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SwarmEvent> SubscribeAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        _logger.LogDebug(
            "StubSwarmCommandBus.SubscribeAsync returning empty stream for tenant {TenantId}",
            tenantId);
        return EmptyAsync(ct);
    }

    private static async IAsyncEnumerable<SwarmEvent> EmptyAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Honour the cancellation token explicitly so a caller that
        // cancels mid-iteration sees the same shape they would from
        // a real subscription.
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
