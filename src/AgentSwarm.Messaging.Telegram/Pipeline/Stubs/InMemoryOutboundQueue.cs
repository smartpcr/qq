using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline.Stubs;

/// <summary>
/// Stage 2.6 in-memory <see cref="IOutboundQueue"/> fallback. A
/// fully-functional in-process queue suitable for dev / CI hosts
/// that have not yet wired the Stage 4.1 persistent
/// <c>PersistentOutboundQueue</c>: the connector's
/// <see cref="TelegramMessengerConnector.SendMessageAsync"/> /
/// <see cref="TelegramMessengerConnector.SendQuestionAsync"/> path
/// can enqueue, the dev <c>OutboundQueueProcessor</c> can dequeue
/// in severity order, and the state-machine transitions
/// (<see cref="MarkSentAsync"/> /
/// <see cref="MarkFailedAsync"/> /
/// <see cref="DeadLetterAsync"/>) update the in-memory record so a
/// running host can complete the full outbound lifecycle without
/// the persistent backstop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Iter-2 evaluator item 3.</b> The previous incarnation of this
/// stub returned <c>null</c> from <see cref="DequeueAsync"/> and
/// treated <see cref="MarkSentAsync"/> / <see cref="MarkFailedAsync"/>
/// as no-ops. That shape silently accepted connector sends that no
/// sender could ever process — a host that booted without the Stage
/// 4.1 persistence module would enqueue messages and then quietly
/// drop them on the floor. This implementation is a real (lossless)
/// FIFO-by-priority queue so dev / CI / integration-test hosts get
/// the same end-to-end shape as production.
/// </para>
/// <para>
/// <b>Replacement contract.</b> Registered in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>
/// via <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}"/>
/// so the Stage 4.1 production registration
/// (<c>AddSingleton&lt;IOutboundQueue, PersistentOutboundQueue&gt;</c>)
/// wins by last-wins semantics. This stub exists so the
/// <see cref="TelegramMessengerConnector"/>'s <c>IOutboundQueue</c>
/// constructor dependency is satisfiable in dev / unit-test bootstraps
/// AND so those bootstraps see realistic enqueue/dequeue behaviour.
/// </para>
/// <para>
/// <b>Idempotency enforcement.</b> The queue rejects duplicate
/// <see cref="OutboundMessage.IdempotencyKey"/>s with
/// <see cref="InvalidOperationException"/> so callers see the
/// same "UNIQUE constraint violated" shape they will get from
/// the production persistent queue's <c>IX_OutboundMessage_IdempotencyKey</c>
/// unique index. Without this, a caller could re-enqueue the
/// same message twice in dev and silently pass tests that fail
/// in production.
/// </para>
/// <para>
/// <b>Dequeue ordering.</b> Highest <see cref="MessageSeverity"/>
/// first (<c>Critical &gt; High &gt; Normal &gt; Low</c>) and within a
/// severity the oldest <see cref="OutboundMessage.CreatedAt"/> first
/// — matches the <see cref="IOutboundQueue.DequeueAsync"/> contract
/// (the Stage 4.1 persistent queue applies the same ordering via
/// SQL <c>ORDER BY Severity DESC, CreatedAt ASC</c>).
/// <see cref="OutboundMessage.NextRetryAt"/> is honoured: a message
/// scheduled for the future is skipped until its retry time has
/// elapsed (using <see cref="TimeProvider.System"/>).
/// </para>
/// <para>
/// <b>State-machine.</b> The record-type contract uses
/// <c>init</c>-only properties so each transition produces a new
/// <see cref="OutboundMessage"/> via <c>with</c> expression and
/// replaces the dictionary entry atomically. Concurrent
/// dequeue+mark from multiple workers is safe via
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/>.
/// </para>
/// </remarks>
internal sealed class InMemoryOutboundQueue : IOutboundQueue
{
    private readonly ConcurrentDictionary<string, byte> _seenIdempotencyKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, OutboundMessage> _byMessageId = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryOutboundQueue()
        : this(TimeProvider.System)
    {
    }

    public InMemoryOutboundQueue(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Visible to tests / diagnostics; ordering is insertion order.</summary>
    internal IReadOnlyCollection<OutboundMessage> Enqueued => _byMessageId.Values.ToList();

    public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_seenIdempotencyKeys.TryAdd(message.IdempotencyKey, 0))
        {
            throw new InvalidOperationException(
                $"OutboundMessage with IdempotencyKey '{message.IdempotencyKey}' already enqueued. "
                + "The production persistent queue's UNIQUE constraint will throw the same shape — "
                + "callers must derive a key that uniquely identifies the logical send (see architecture.md §3.1).");
        }
        if (!_byMessageId.TryAdd(message.MessageId, message))
        {
            // MessageId is a fresh Guid per enqueue, so a collision here
            // is a logic bug rather than a dedup case — surface it.
            _seenIdempotencyKeys.TryRemove(message.IdempotencyKey, out _);
            throw new InvalidOperationException(
                $"OutboundMessage MessageId '{message.MessageId}' already present — MessageId collisions indicate a caller reused a Guid instead of generating a fresh one.");
        }
        return Task.CompletedTask;
    }

    public Task<OutboundMessage?> DequeueAsync(CancellationToken ct)
    {
        // Loop (rather than tail-recurse) on CAS failure. C# does not
        // guarantee tail-call optimisation, and the previous comment's
        // "bounded recursion: walks the entire queue once" reasoning
        // only holds in the absence of concurrent mutation. Under
        // sustained contention from multiple OutboundQueueProcessor
        // workers — or interleaved EnqueueAsync calls that introduce
        // fresh Pending records between snapshots — TryUpdate can fail
        // repeatedly and grow the call stack without bound, eventually
        // triggering StackOverflowException. A while-loop is equivalent
        // in behaviour (each iteration re-snapshots and re-claims) with
        // O(1) stack usage.
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var now = _timeProvider.GetUtcNow();

            // Order: highest-severity first, then oldest-created first.
            // The MessageSeverity enum is declared in ascending priority
            // order (Critical=0, High=1, Normal=2, Low=3) so "highest
            // severity" corresponds to the LOWEST ordinal — OrderBy, not
            // OrderByDescending. Matches IOutboundQueue.DequeueAsync
            // contract ("Critical > High > Normal > Low") and the Stage 4.1
            // SQL ORDER BY clause (which sorts by an explicit priority
            // mapping table for the same reason).
            var candidate = _byMessageId.Values
                .Where(m => m.Status == OutboundMessageStatus.Pending
                            && (m.NextRetryAt is null || m.NextRetryAt <= now))
                .OrderBy(m => m.Severity)
                .ThenBy(m => m.CreatedAt)
                .FirstOrDefault();

            if (candidate is null)
            {
                return Task.FromResult<OutboundMessage?>(null);
            }

            // Claim atomically: transition Pending → Sending via TryUpdate.
            // If another worker beats us to it (TryUpdate returns false
            // because the dictionary entry no longer equals our snapshot),
            // loop and re-pick the next-best candidate.
            var claimed = candidate with { Status = OutboundMessageStatus.Sending };
            if (_byMessageId.TryUpdate(candidate.MessageId, claimed, candidate))
            {
                return Task.FromResult<OutboundMessage?>(claimed);
            }
        }
    }

    public Task MarkSentAsync(Guid messageId, long telegramMessageId, CancellationToken ct)
    {
        if (_byMessageId.TryGetValue(messageId, out var current))
        {
            var sent = current with
            {
                Status = OutboundMessageStatus.Sent,
                SentAt = _timeProvider.GetUtcNow(),
                TelegramMessageId = telegramMessageId,
            };
            _byMessageId.TryUpdate(messageId, sent, current);
        }
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct)
    {
        if (_byMessageId.TryGetValue(messageId, out var current))
        {
            var nextAttempt = current.AttemptCount + 1;
            var hasBudgetLeft = nextAttempt < current.MaxAttempts;
            var failed = current with
            {
                Status = hasBudgetLeft ? OutboundMessageStatus.Pending : OutboundMessageStatus.Failed,
                AttemptCount = nextAttempt,
                ErrorDetail = error,
                // Simple exponential-style backoff at second resolution;
                // production replaces this with the Stage 4.1 RetryPolicy.
                NextRetryAt = hasBudgetLeft
                    ? _timeProvider.GetUtcNow().AddSeconds(Math.Min(60, 1 << Math.Min(nextAttempt, 6)))
                    : null,
            };
            _byMessageId.TryUpdate(messageId, failed, current);
        }
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(Guid messageId, CancellationToken ct)
    {
        if (_byMessageId.TryGetValue(messageId, out var current))
        {
            var dead = current with { Status = OutboundMessageStatus.DeadLettered };
            _byMessageId.TryUpdate(messageId, dead, current);
        }
        return Task.CompletedTask;
    }
}
