// -----------------------------------------------------------------------
// <copyright file="DeduplicationCleanupService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 4.3 — periodic purge driver for the
/// <see cref="PersistentDeduplicationService"/>'s
/// <c>processed_events</c> table. Wakes on the
/// <see cref="DeduplicationOptions.PurgeInterval"/> cadence and deletes
/// rows whose effective timestamp
/// (<c>COALESCE(ProcessedAt, ReservedAt)</c>) is older than
/// <see cref="DeduplicationOptions.EntryTimeToLive"/>. Keeps the
/// table size bounded even under sustained burst load — the
/// implementation-plan brief mandates "periodic purge of expired
/// entries" for the production EF backend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime + scope.</b> Registered as a hosted service in
/// <see cref="ServiceCollectionExtensions.AddMessagingPersistence"/>;
/// uses <see cref="IServiceScopeFactory"/> to open a fresh DI scope
/// for each sweep so the scoped <see cref="MessagingDbContext"/> can
/// be resolved without violating the captive-dependency rule.
/// </para>
/// <para>
/// <b>Failure isolation.</b> Each sweep is wrapped in a single
/// try / catch so a transient DB outage during cleanup does not
/// terminate the hosted service loop. The cadence is much slower
/// than the live INSERT path, so a missed sweep merely defers
/// eviction by one cycle (rows remain queryable but the table grows
/// proportionally to the missed interval).
/// </para>
/// <para>
/// <b>Test seam.</b> The internal <see cref="RunSweepAsync"/>
/// method is exposed so unit tests can exercise the purge query
/// against a SQLite in-memory database without spinning up the
/// hosted service loop.
/// </para>
/// </remarks>
public sealed class DeduplicationCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<DeduplicationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeduplicationCleanupService> _logger;

    public DeduplicationCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<DeduplicationOptions> options,
        TimeProvider timeProvider,
        ILogger<DeduplicationCleanupService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.CurrentValue.PurgeInterval;
        if (interval <= TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Deduplication cleanup is disabled (PurgeInterval={Interval}); hosted service will idle until shutdown.",
                interval);
            return;
        }

        _logger.LogInformation(
            "Deduplication cleanup loop started; PurgeInterval={Interval}, EntryTtl={Ttl}.",
            interval,
            _options.CurrentValue.EntryTimeToLive);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var evicted = await RunSweepAsync(stoppingToken).ConfigureAwait(false);
                if (evicted > 0)
                {
                    _logger.LogInformation(
                        "Deduplication cleanup evicted {Evicted} processed_events rows older than {Ttl}.",
                        evicted,
                        _options.CurrentValue.EntryTimeToLive);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Swallow + log so a transient DB outage does not
                // terminate the hosted service loop. The next sweep
                // attempts the purge again.
                _logger.LogError(
                    ex,
                    "Deduplication cleanup sweep failed; will retry after PurgeInterval={Interval}.",
                    interval);
            }
        }
    }

    /// <summary>
    /// Runs a single purge sweep. Exposed for unit tests so the
    /// eviction predicate can be exercised deterministically against
    /// a SQLite in-memory database without driving the hosted service
    /// loop or waiting on the timer. Returns the number of evicted
    /// rows.
    /// </summary>
    public async Task<int> RunSweepAsync(CancellationToken ct)
    {
        var ttl = _options.CurrentValue.EntryTimeToLive;
        if (ttl <= TimeSpan.Zero)
        {
            return 0;
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - ttl;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Single bulk DELETE — no materialization of expired keys
        // into memory and no N-row IN (...) clause that could explode
        // under a long-outage / burst-recovery sweep. EF Core 7+
        // ExecuteDeleteAsync compiles the predicate to a single
        // SQL DELETE statement that the database server evaluates
        // server-side; row count is returned by the provider.
        //
        // The predicate is the COALESCE(processed_at, reserved_at) < cutoff
        // sliding-window contract, written in its expanded
        // (`processed_at != null AND processed_at < cutoff)
        //   OR (processed_at == null AND reserved_at < cutoff)`
        // form so SQLite, SQL Server, and PostgreSQL all translate
        // it to a clean two-branch WHERE without provider-specific
        // null-handling surprises in the `??` translator. The
        // composite index ix_processed_events_processed_reserved
        // keeps the scan narrow even under burst load.
        var deleted = await db.ProcessedEvents
            .Where(x =>
                (x.ProcessedAt != null && x.ProcessedAt < cutoff)
                || (x.ProcessedAt == null && x.ReservedAt < cutoff))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogDebug(
                "Deduplication sweep at cutoff {Cutoff} removed {Deleted} rows.",
                cutoff,
                deleted);
        }
        return deleted;
    }
}
