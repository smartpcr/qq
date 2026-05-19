using AgentSwarm.Messaging.Teams.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Background hosted service that periodically evicts expired entries from the
/// <see cref="ProcessedCardActionSet"/>. Implements Stage 6.2 step 3 of
/// <c>implementation-plan.md</c> (Duplicate Suppression and Idempotency).
/// </summary>
/// <remarks>
/// <para>
/// Cadence is governed by <see cref="CardActionDedupeOptions.EvictionInterval"/>
/// (defaults to 5 minutes). On each tick the service asks the shared
/// <see cref="ProcessedCardActionSet"/> to <see cref="ProcessedCardActionSet.EvictExpired(DateTimeOffset)"/>;
/// stale entries (older than <see cref="CardActionDedupeOptions.EntryLifetime"/>) are
/// removed, and the count is structured-logged at debug level for observability.
/// </para>
/// <para>
/// The service follows the canonical .NET <see cref="BackgroundService"/> contract: the
/// loop terminates on <see cref="CancellationToken"/> cancellation (host shutdown) and
/// swallows transient eviction exceptions so a one-off failure cannot crash the host.
/// </para>
/// <para>
/// <b>Stage 6.3 iter-10 evaluator fix item 1.</b> The lifecycle / tick / failure
/// logs are emitted inside <see cref="TeamsLogScope.BeginScope"/> so each entry
/// carries the canonical <c>CorrelationId</c> / <c>TenantId</c> / <c>UserId</c>
/// enrichment keys per §6.3 step 5. Background workers have no per-message
/// context, so the helper substitutes <see cref="TeamsLogScope.EmptyValueSentinel"/>
/// (<c>"-"</c>) for each key — dashboards that filter on the enrichment never
/// see a missing slot.
/// </para>
/// </remarks>
public sealed class ProcessedCardActionEvictionService : BackgroundService
{
    private readonly ProcessedCardActionSet _processedActions;
    private readonly CardActionDedupeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessedCardActionEvictionService> _logger;

    /// <summary>Construct the eviction service.</summary>
    public ProcessedCardActionEvictionService(
        ProcessedCardActionSet processedActions,
        CardActionDedupeOptions options,
        TimeProvider timeProvider,
        ILogger<ProcessedCardActionEvictionService> logger)
    {
        _processedActions = processedActions ?? throw new ArgumentNullException(nameof(processedActions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_options.EvictionInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(CardActionDedupeOptions.EvictionInterval)} must be strictly positive; got {_options.EvictionInterval}.",
                nameof(options));
        }
    }

    /// <summary>
    /// Eviction interval (5 minutes by default per the Stage 6.2 brief). Exposed for
    /// tests that need to assert configuration without instantiating the service.
    /// </summary>
    internal TimeSpan EvictionInterval => _options.EvictionInterval;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stage 6.3 iter-10 evaluator fix item 1 — wrap the entire ExecuteAsync body
        // in a TeamsLogScope so every lifecycle / tick / failure log emitted by the
        // hosted-service worker carries the canonical three-key enrichment.
        // Background workers have no per-message correlation / tenant / user, so the
        // helper substitutes EmptyValueSentinel ("-") into each slot.
        using var workerLogScope = TeamsLogScope.BeginScope(_logger);

        _logger.LogInformation(
            "ProcessedCardActionEvictionService started — entry lifetime {EntryLifetime}, eviction cadence {EvictionInterval}.",
            _options.EntryLifetime,
            _options.EvictionInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.EvictionInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var removed = _processedActions.EvictExpired(_timeProvider.GetUtcNow());
                if (removed > 0)
                {
                    _logger.LogDebug(
                        "ProcessedCardActionEvictionService evicted {Removed} expired entries; current count {Remaining}.",
                        removed,
                        _processedActions.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ProcessedCardActionEvictionService eviction tick threw; continuing on the next cadence interval.");
            }
        }

        _logger.LogInformation("ProcessedCardActionEvictionService stopped.");
    }
}
