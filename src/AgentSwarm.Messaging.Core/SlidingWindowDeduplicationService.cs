// -----------------------------------------------------------------------
// <copyright file="SlidingWindowDeduplicationService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 4.3 — in-memory sliding-window
/// <see cref="IDeduplicationService"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> with a periodic
/// cleanup timer. The brief calls this out as the "development"
/// backend variant of the production EF-backed
/// <c>DeduplicationService</c>; hosts that wire this implementation
/// get the same sliding-window TTL semantics as the persistent backend
/// without depending on EF Core or a SQLite file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default registration.</b> <c>AddTelegram</c> wires this class
/// as the default <see cref="IDeduplicationService"/> implementation
/// for dev / local hosts (per implementation-plan.md Stage 4.3,
/// step 2). Production hosts that also call <c>AddMessagingPersistence</c>
/// see the registration replaced last-wins with the EF-backed
/// <c>PersistentDeduplicationService</c>.
/// </para>
/// <para>
/// <b>Concurrency.</b> Same atomic-claim contract as the persistent
/// sibling: <see cref="TryReserveAsync"/> is a single
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> on the
/// reservation map; a concurrent burst of N callers for the same
/// event id sees exactly one winner.
/// </para>
/// <para>
/// <b>Eviction.</b> The cleanup timer runs at
/// <see cref="DeduplicationOptions.PurgeInterval"/> and removes
/// entries whose effective timestamp
/// (<c>ProcessedAt</c> when set, else <c>ReservedAt</c>) is older
/// than <see cref="DeduplicationOptions.EntryTimeToLive"/>. The
/// class is disposable so a host that stops without disposing the
/// service still releases the timer when the process exits.
/// </para>
/// </remarks>
public sealed class SlidingWindowDeduplicationService : IDeduplicationService, IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly DeduplicationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SlidingWindowDeduplicationService> _logger;
    private readonly ITimer? _cleanupTimer;
    private int _disposed;

    public SlidingWindowDeduplicationService(
        IOptions<DeduplicationOptions> options,
        TimeProvider timeProvider,
        ILogger<SlidingWindowDeduplicationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? new DeduplicationOptions();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<SlidingWindowDeduplicationService>.Instance;

        if (_options.PurgeInterval > TimeSpan.Zero)
        {
            _cleanupTimer = _timeProvider.CreateTimer(
                _ => Purge(),
                state: null,
                dueTime: _options.PurgeInterval,
                period: _options.PurgeInterval);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryReserveAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);
        var now = _timeProvider.GetUtcNow();
        var added = _entries.TryAdd(eventId, new Entry(now, null));
        return Task.FromResult(added);
    }

    /// <inheritdoc />
    public Task ReleaseReservationAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);
        if (_entries.TryGetValue(eventId, out var existing) && existing.ProcessedAt is null)
        {
            // Sticky-processed guard: only remove when the row is in
            // the pre-processed reservation phase. We compare against
            // the captured snapshot to avoid racing with a concurrent
            // MarkProcessedAsync.
            _entries.TryRemove(new KeyValuePair<string, Entry>(eventId, existing));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsProcessedAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);
        var result = _entries.TryGetValue(eventId, out var entry) && entry.ProcessedAt is not null;
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task MarkProcessedAsync(string eventId, CancellationToken ct)
    {
        ValidateEventId(eventId);
        var now = _timeProvider.GetUtcNow();

        // AddOrUpdate keeps the original ReservedAt when a prior
        // reservation exists, and stamps the same time as both
        // ReservedAt and ProcessedAt when the marker is written
        // without a prior reservation (tooling-replay path).
        _entries.AddOrUpdate(
            eventId,
            _ => new Entry(now, now),
            (_, existing) => existing.ProcessedAt is not null
                ? existing
                : existing with { ProcessedAt = now });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Exposed for tests so the sweep contract can be exercised
    /// deterministically without waiting on the timer period. Removes
    /// every entry whose effective timestamp is older than
    /// <see cref="DeduplicationOptions.EntryTimeToLive"/>. Returns the
    /// number of evicted rows.
    /// </summary>
    public int Purge()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return 0;
        }

        var cutoff = _timeProvider.GetUtcNow() - _options.EntryTimeToLive;
        var evicted = 0;
        foreach (var pair in _entries)
        {
            var effective = pair.Value.ProcessedAt ?? pair.Value.ReservedAt;
            if (effective < cutoff
                && _entries.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value)))
            {
                evicted++;
            }
        }

        if (evicted > 0)
        {
            _logger.LogDebug(
                "Sliding-window deduplication purge evicted {Evicted} entries older than TTL {Ttl}.",
                evicted,
                _options.EntryTimeToLive);
        }

        return evicted;
    }

    /// <summary>
    /// Test-only — snapshot of the current entry count. Avoids
    /// exposing the live ConcurrentDictionary to consumers.
    /// </summary>
    internal int Count => _entries.Count;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cleanupTimer?.Dispose();
    }

    private static void ValidateEventId(string eventId)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            throw new ArgumentException("eventId must be non-null and non-empty.", nameof(eventId));
        }
    }

    /// <summary>
    /// Per-entry tuple capturing the reservation and (optional)
    /// processed timestamps. <see cref="ProcessedAt"/> is <c>null</c>
    /// while the row is in the pre-processed reservation phase.
    /// </summary>
    private readonly record struct Entry(DateTimeOffset ReservedAt, DateTimeOffset? ProcessedAt);
}
