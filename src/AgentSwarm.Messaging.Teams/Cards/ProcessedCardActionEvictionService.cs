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
