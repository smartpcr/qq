using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline.Stubs;

/// <summary>
/// Stage 2.2 in-memory stub <see cref="IPendingDisambiguationStore"/>
/// backed by a single <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Lets the pipeline run end-to-end before Stage 5.3 wires the
/// persistent replacement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity (single-use callback).</b> <see cref="TakeAsync"/> uses
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>
/// — the canonical atomic remove-on-read primitive — so two concurrent
/// callbacks for the same token cannot both succeed. Closes the
/// "replayed callback re-triggers the original command" risk and
/// matches the contract semantics required of the production replacement.
/// </para>
/// <para>
/// <b>Expiry handling.</b> <see cref="TakeAsync"/> treats an expired
/// entry as "not found" (returns <c>null</c>) AND removes it on the way
/// out so the dictionary does not accumulate stale entries even when
/// <see cref="PurgeExpiredAsync"/> is never called. The pipeline
/// supplies <see cref="DateTimeOffset.UtcNow"/> via
/// <see cref="TimeProvider"/> so tests can pin time.
/// </para>
/// <para>
/// <b>Process-local.</b> Restart loses every pending entry — a stale
/// callback after a process recycle simply returns <c>null</c> from
/// <see cref="TakeAsync"/> and the operator gets the standard "callback
/// expired" reply. The Stage 5.3 persistent replacement closes that gap.
/// </para>
/// </remarks>
internal sealed class InMemoryPendingDisambiguationStore : IPendingDisambiguationStore
{
    private readonly ConcurrentDictionary<string, PendingDisambiguation> _entries =
        new(StringComparer.Ordinal);

    private readonly TimeProvider _timeProvider;

    public InMemoryPendingDisambiguationStore()
        : this(TimeProvider.System)
    {
    }

    public InMemoryPendingDisambiguationStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task StoreAsync(PendingDisambiguation entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_entries.TryAdd(entry.Token, entry))
        {
            throw new InvalidOperationException(
                $"Disambiguation token '{entry.Token}' is already registered. "
                + "The pipeline must generate collision-free tokens; a duplicate here indicates a generator bug.");
        }

        return Task.CompletedTask;
    }

    public Task<PendingDisambiguation?> TakeAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("token must be non-null and non-empty.", nameof(token));
        }

        if (!_entries.TryRemove(token, out var entry))
        {
            return Task.FromResult<PendingDisambiguation?>(null);
        }

        if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            // Already removed above — treat as "not found" so the caller
            // does not re-issue the now-stale command.
            return Task.FromResult<PendingDisambiguation?>(null);
        }

        return Task.FromResult<PendingDisambiguation?>(entry);
    }

    public Task PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }

        return Task.CompletedTask;
    }
}
