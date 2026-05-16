using System.Collections.Concurrent;
using Microsoft.Bot.Schema;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// In-memory processed-action set for Stage 6.2 of <c>implementation-plan.md</c>
/// (Duplicate Suppression and Idempotency) — layer 2 of the three-layer idempotency
/// model described in <c>architecture.md</c> §2.6 / §6.3:
/// </summary>
/// <list type="number">
/// <item><description>Layer 1 — transport-level <c>ActivityDeduplicationMiddleware</c>
/// keyed on <see cref="Activity.Id"/>.</description></item>
/// <item><description>Layer 2 — <i>this</i> domain-level fast-path keyed on
/// <c>(QuestionId, UserId)</c>.</description></item>
/// <item><description>Layer 3 — durable <c>AgentQuestion.Status</c> compare-and-set
/// via <c>IAgentQuestionStore.TryUpdateStatusAsync</c>.</description></item>
/// </list>
/// <remarks>
/// <para>
/// <b>Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.</b> The key is a value
/// tuple <c>(string QuestionId, string UserId)</c> per the canonical brief, and the value
/// is an <see cref="Entry"/> capturing the timestamp at which the action was first
/// recorded plus the prior <see cref="AdaptiveCardInvokeResponse"/> so a duplicate
/// submission can <i>return the previous result</i> rather than producing a generic
/// rejection (Stage 6.2 step 2).
/// </para>
/// <para>
/// <b>Entry lifetime.</b> Entries live for <see cref="CardActionDedupeOptions.EntryLifetime"/>
/// (24 hours by default). Eviction runs on the
/// <see cref="ProcessedCardActionEvictionService"/> background timer
/// (5 minute cadence by default) which calls <see cref="EvictExpired(DateTimeOffset)"/>.
/// Callers may also evict a single entry via <see cref="Remove"/> — the
/// <see cref="Cards.CardActionHandler"/> uses this to release the slot when an
/// unhandled exception in the pipeline aborts before reaching a terminal outcome, so the
/// user can retry.
/// </para>
/// <para>
/// <b>Singleton.</b> The DI graph registers a single instance shared by every
/// <see cref="Cards.CardActionHandler"/> resolution and by the eviction service. The
/// background timer service depends on the same singleton.
/// </para>
/// </remarks>
public sealed class ProcessedCardActionSet
{
    private readonly ConcurrentDictionary<(string QuestionId, string UserId), Entry> _entries =
        new(ProcessedCardActionKeyComparer.Instance);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _entryLifetime;

    /// <summary>
    /// Construct the set with the canonical 24-hour entry lifetime and
    /// <see cref="TimeProvider.System"/>. Used by DI when the options-aware ctor cannot
    /// resolve a <see cref="CardActionDedupeOptions"/> singleton.
    /// </summary>
    public ProcessedCardActionSet()
        : this(new CardActionDedupeOptions(), TimeProvider.System)
    {
    }

    /// <summary>
    /// Construct the set with the supplied options and clock. Used by DI and by unit
    /// tests that need a deterministic clock or a shortened lifetime to exercise the
    /// eviction loop without waiting 24 hours.
    /// </summary>
    public ProcessedCardActionSet(CardActionDedupeOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (options.EntryLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(CardActionDedupeOptions.EntryLifetime)} must be strictly positive; got {options.EntryLifetime}.",
                nameof(options));
        }

        _timeProvider = timeProvider;
        _entryLifetime = options.EntryLifetime;
    }

    /// <summary>Current entry count — used by tests and observability.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Configured per-entry lifetime — exposed so the background eviction service can
    /// share the same value rather than duplicating the options snapshot.
    /// </summary>
    public TimeSpan EntryLifetime => _entryLifetime;

    /// <summary>
    /// Attempt to claim the dedupe slot for the supplied <paramref name="key"/>.
    /// Returns a <see cref="ClaimResult"/> capturing whether the caller now owns the
    /// slot (<see cref="ClaimResult.IsOwner"/> = <c>true</c>) — in which case they must
    /// run the full card-action pipeline and report the terminal response via
    /// <see cref="RecordResult"/> on success or <see cref="Remove"/> on failure — or
    /// whether another caller already owns the slot, in which case
    /// <see cref="ClaimResult.PreviousResponseTask"/> resolves to the prior caller's
    /// terminal response. The task completes synchronously when a prior
    /// <see cref="RecordResult"/> has already cached the response; it completes
    /// asynchronously when the prior caller is still in flight, so a concurrent second
    /// invocation that arrives <i>before</i> the first finishes still receives the same
    /// terminal response per the Stage 6.2 step 2 contract ("return the previous result
    /// without re-executing"). When the prior caller fails (and calls
    /// <see cref="Remove"/>), the task resolves to <c>null</c>, signalling that the
    /// waiter should retry the claim.
    /// </summary>
    /// <param name="key">The <c>(QuestionId, UserId)</c> pair identifying the action.</param>
    public ClaimResult Claim((string QuestionId, string UserId) key)
    {
        ValidateKey(key);

        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            var fresh = new Entry(now);
            if (_entries.TryAdd(key, fresh))
            {
                return new ClaimResult(IsOwner: true, PreviousResponseTask: _completedNullTask);
            }

            if (_entries.TryGetValue(key, out var existing))
            {
                return new ClaimResult(IsOwner: false, PreviousResponseTask: existing.CompletionSource.Task);
            }

            // Race: between TryAdd failing and TryGetValue, another caller removed the
            // entry (Remove or EvictExpired). Loop and re-claim.
        }
    }

    /// <summary>
    /// Synchronous best-effort overload preserved for callers (and tests) written
    /// against the iter-1 API. Returns <c>true</c> when the caller owns the slot;
    /// returns <c>false</c> when an entry already exists, in which case
    /// <paramref name="previousResponse"/> is populated only when the prior caller has
    /// already cached a terminal response (a concurrent in-flight first caller leaves
    /// it <c>null</c>; use <see cref="Claim"/> + <see cref="ClaimResult.PreviousResponseTask"/>
    /// to await the in-flight response instead).
    /// </summary>
    public bool TryClaim(
        (string QuestionId, string UserId) key,
        out AdaptiveCardInvokeResponse? previousResponse)
    {
        var claim = Claim(key);
        if (claim.IsOwner)
        {
            previousResponse = null;
            return true;
        }

        previousResponse = claim.PreviousResponseTask.IsCompletedSuccessfully
            ? claim.PreviousResponseTask.GetAwaiter().GetResult()
            : null;
        return false;
    }

    /// <summary>
    /// Record the terminal response from a successful card-action pipeline run. The
    /// cached response replays for both already-completed callers (via the next
    /// <see cref="Claim"/> call) and for any concurrent waiters that observed the slot
    /// while the pipeline was still running (their
    /// <see cref="ClaimResult.PreviousResponseTask"/> completes with this response).
    /// Idempotent — re-invoking with a fresh response overwrites the cached value but
    /// the original waiters keep the value they were signalled with.
    /// </summary>
    public void RecordResult((string QuestionId, string UserId) key, AdaptiveCardInvokeResponse response)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(response);
        var now = _timeProvider.GetUtcNow();

        if (_entries.TryGetValue(key, out var existing))
        {
            existing.RecordedAt = now;
            if (existing.CompletionSource.TrySetResult(response))
            {
                return;
            }

            // TCS was already completed by a prior RecordResult; replace the entry with
            // a fresh pre-completed one so the next Claim observes the latest response.
            // In-flight waiters that already pulled the prior TCS keep that value —
            // first-wins is the correct concurrency semantic for waiters.
            var replaced = new Entry(now);
            replaced.CompletionSource.TrySetResult(response);
            _entries[key] = replaced;
            return;
        }

        // No prior entry (caller never claimed or the entry was evicted between Claim
        // and RecordResult). Create a fresh entry whose TCS is pre-completed so
        // subsequent claimants replay the response immediately.
        var seeded = new Entry(now);
        seeded.CompletionSource.TrySetResult(response);
        _entries[key] = seeded;
    }

    /// <summary>
    /// Remove the entry for <paramref name="key"/> (used by the handler to roll back the
    /// dedupe slot when an unhandled exception aborts the pipeline so the user can
    /// retry, or when a transient/non-durable rejection should not block subsequent
    /// valid submissions). No-op if the entry is absent. Concurrent waiters observing
    /// the removed entry's <see cref="ClaimResult.PreviousResponseTask"/> resolve to
    /// <c>null</c>, signalling that they should re-claim and run the pipeline
    /// themselves.
    /// </summary>
    public void Remove((string QuestionId, string UserId) key)
    {
        ValidateKey(key);
        if (_entries.TryRemove(key, out var existing))
        {
            // Signal in-flight waiters that no result will be cached so they retry.
            existing.CompletionSource.TrySetResult(null);
        }
    }

    /// <summary>
    /// Purge every entry whose age exceeds <see cref="EntryLifetime"/> as of
    /// <paramref name="now"/>. Returns the number of entries removed — used by the
    /// background eviction service for structured-log diagnostics. Concurrent waiters
    /// on evicted entries are signalled with <c>null</c> so they retry.
    /// </summary>
    public int EvictExpired(DateTimeOffset now)
    {
        if (_entries.IsEmpty)
        {
            return 0;
        }

        var removed = 0;
        foreach (var kvp in _entries)
        {
            if (now - kvp.Value.RecordedAt > _entryLifetime)
            {
                if (_entries.TryRemove(kvp.Key, out var existing))
                {
                    existing.CompletionSource.TrySetResult(null);
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Purge every entry whose age exceeds <see cref="EntryLifetime"/> as of the
    /// injected clock's current instant. Convenience overload for tests.
    /// </summary>
    public int EvictExpired() => EvictExpired(_timeProvider.GetUtcNow());

    private static void ValidateKey((string QuestionId, string UserId) key)
    {
        if (string.IsNullOrEmpty(key.QuestionId))
        {
            throw new ArgumentException("Dedupe key requires a non-empty QuestionId.", nameof(key));
        }

        if (string.IsNullOrEmpty(key.UserId))
        {
            throw new ArgumentException("Dedupe key requires a non-empty UserId.", nameof(key));
        }
    }

    private static readonly Task<AdaptiveCardInvokeResponse?> _completedNullTask =
        Task.FromResult<AdaptiveCardInvokeResponse?>(null);

    /// <summary>
    /// Outcome of a <see cref="Claim"/> attempt. When <see cref="IsOwner"/> is
    /// <c>true</c>, the caller owns the slot and must report the terminal response via
    /// <see cref="ProcessedCardActionSet.RecordResult"/> on success or
    /// <see cref="ProcessedCardActionSet.Remove"/> on failure. When <c>false</c>, the
    /// caller awaits <see cref="PreviousResponseTask"/> to receive the prior caller's
    /// response (or <c>null</c> when the prior caller failed and the slot was
    /// released).
    /// </summary>
    public readonly record struct ClaimResult(bool IsOwner, Task<AdaptiveCardInvokeResponse?> PreviousResponseTask);

    private sealed class Entry
    {
        public DateTimeOffset RecordedAt { get; set; }
        public TaskCompletionSource<AdaptiveCardInvokeResponse?> CompletionSource { get; }

        public Entry(DateTimeOffset recordedAt)
        {
            RecordedAt = recordedAt;
            CompletionSource = new TaskCompletionSource<AdaptiveCardInvokeResponse?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class ProcessedCardActionKeyComparer : IEqualityComparer<(string QuestionId, string UserId)>
    {
        public static readonly ProcessedCardActionKeyComparer Instance = new();

        public bool Equals((string QuestionId, string UserId) x, (string QuestionId, string UserId) y)
            => StringComparer.Ordinal.Equals(x.QuestionId, y.QuestionId)
            && StringComparer.Ordinal.Equals(x.UserId, y.UserId);

        public int GetHashCode((string QuestionId, string UserId) obj)
            => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.QuestionId),
                StringComparer.Ordinal.GetHashCode(obj.UserId));
    }
}
