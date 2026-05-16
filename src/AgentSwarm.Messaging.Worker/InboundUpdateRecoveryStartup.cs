using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Worker;

/// <summary>
/// One-shot <see cref="IHostedService"/> that runs at host startup and
/// calls <see cref="IInboundUpdateStore.ResetInterruptedAsync"/> to
/// convert any <see cref="IdempotencyStatus.Processing"/> rows left
/// behind by a process crash back to <see cref="IdempotencyStatus.Received"/>.
/// Registered BEFORE <see cref="InboundUpdateDispatcher"/> and
/// <see cref="InboundRecoverySweep"/> so the new process starts from a
/// clean slate where the only Processing rows are ones the live
/// dispatcher claimed in this generation.
/// </summary>
/// <remarks>
/// <b>Why this is still needed even though <c>GetRecoverableAsync</c>
/// now includes <c>Processing</c>.</b> The recovery query
/// (architecture.md §4.8) deliberately surfaces <c>Processing</c> rows
/// so the sweep can reclaim crash-orphaned work, and the
/// <see cref="IInboundUpdateStore.TryMarkProcessingAsync"/> CAS
/// arbitrates dispatcher-vs-sweep races on live rows. This startup
/// reset narrows the race window further: by flipping crash-stuck
/// <c>Processing</c> back to <c>Received</c> at boot we avoid a
/// situation where the new process's first sweep tick observes
/// thousands of stale <c>Processing</c> rows that all CAS-fail (wasting
/// a sweep pass) and the operator sees confusing "skipped"
/// telemetry. The reset is fast (single conditional UPDATE) and
/// idempotent (running it twice is a no-op on the second pass).
/// </remarks>
internal sealed class InboundUpdateRecoveryStartup : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InboundUpdateRecoveryStartup> _logger;

    public InboundUpdateRecoveryStartup(
        IServiceScopeFactory scopeFactory,
        ILogger<InboundUpdateRecoveryStartup> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
        var resetCount = await store.ResetInterruptedAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "InboundUpdateRecoveryStartup completed. ResetCount={ResetCount}", resetCount);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
