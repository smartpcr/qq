using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// Background hosted service that periodically purges expired entries from the
/// <see cref="OutboundMessageDeduplicator"/>. Companion to Stage 6.2 step 4 of
/// <c>implementation-plan.md</c> — alongside lazy in-line eviction inside
/// <see cref="OutboundMessageDeduplicator.TryRegister"/>, this timer ensures the
/// in-memory store does not retain stale entries indefinitely on a quiet system.
/// </summary>
public sealed class OutboundDeduplicationEvictionService : BackgroundService
{
    private readonly OutboundMessageDeduplicator _deduplicator;
    private readonly OutboundDeduplicationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboundDeduplicationEvictionService> _logger;

    /// <summary>Construct the eviction service.</summary>
    public OutboundDeduplicationEvictionService(
        OutboundMessageDeduplicator deduplicator,
        OutboundDeduplicationOptions options,
        TimeProvider timeProvider,
        ILogger<OutboundDeduplicationEvictionService> logger)
    {
        _deduplicator = deduplicator ?? throw new ArgumentNullException(nameof(deduplicator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_options.EvictionInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(OutboundDeduplicationOptions.EvictionInterval)} must be strictly positive; got {_options.EvictionInterval}.",
                nameof(options));
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboundDeduplicationEvictionService started — window {Window}, eviction cadence {EvictionInterval}.",
            _options.Window,
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
                var removed = _deduplicator.EvictExpired(_timeProvider.GetUtcNow());
                if (removed > 0)
                {
                    _logger.LogDebug(
                        "OutboundDeduplicationEvictionService evicted {Removed} expired entries; current count {Remaining}.",
                        removed,
                        _deduplicator.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "OutboundDeduplicationEvictionService eviction tick threw; continuing on the next cadence interval.");
            }
        }

        _logger.LogInformation("OutboundDeduplicationEvictionService stopped.");
    }
}
