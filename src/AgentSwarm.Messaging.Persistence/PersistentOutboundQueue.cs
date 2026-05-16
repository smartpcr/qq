using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core-backed implementation of <see cref="IOutboundQueue"/>. Persists
/// every outbound message before delivery so the system survives process
/// crashes; enforces priority ordering and idempotency-key collapsing per
/// architecture.md §3.2 / §4.4 / §10.3 / §10.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 2.2 scope:</b> this stage lands the enqueue / dequeue / count
/// surface (severity-ordered pickup, idempotency-key UNIQUE collapse,
/// priority-aware batching helpers) used by the inbound pipeline and the
/// dispatcher. Stage 2.3 layers in retry / dead-letter behaviour on
/// <see cref="MarkSentAsync"/> / <see cref="MarkFailedAsync"/> /
/// <see cref="DeadLetterAsync"/>; baseline implementations of those three
/// land here so the contract is satisfied end-to-end and the Worker stage
/// can register the queue against the interface without compile-time gaps.
/// </para>
/// <para>
/// <b>Concurrent dispatcher safety:</b> <see cref="DequeueAsync"/> and
/// <see cref="DequeueBatchAsync"/> claim each row via a conditional
/// <c>UPDATE ... WHERE Status = observed</c> issued through EF Core's
/// <see cref="EntityFrameworkQueryableExtensions.ExecuteUpdateAsync"/>.
/// The conditional predicate makes the claim atomic: two dispatchers that
/// observe the same Pending/Failed/Sending candidate will both attempt the
/// UPDATE, but only one will see <c>rowsAffected == 1</c>; the loser sees
/// <c>0</c> and falls through to the next candidate. No row is ever
/// returned to more than one dispatcher.
/// </para>
/// <para>
/// <b>Crash-window recovery (architecture.md §10.3 / Gap A §238):</b> on
/// successful claim the dispatcher stamps a lease via
/// <see cref="OutboundMessage.NextRetryAt"/> (default 5 minutes). If the
/// process crashes after the claim but before
/// <see cref="MarkSentAsync"/> / <see cref="MarkFailedAsync"/>, the row
/// stays in <see cref="OutboundMessageStatus.Sending"/> with an elapsed
/// lease; the next <see cref="DequeueAsync"/> call treats expired-lease
/// Sending rows as eligible candidates and re-claims them for redelivery.
/// At-least-once semantics are documented as acceptable in architecture.md
/// §10.3 Gap A because the receiving Discord pipeline keys on
/// <see cref="OutboundMessage.IdempotencyKey"/> / message snowflake at the
/// next layer.
/// </para>
/// </remarks>
public sealed class PersistentOutboundQueue : IOutboundQueue
{
    /// <summary>
    /// Default lease window granted to a dispatcher when it claims a row
    /// for sending. If the dispatcher crashes (or hangs) past this window,
    /// the next sweep re-claims the row. Five minutes comfortably covers
    /// the worst-case Discord REST latency plus a generous retry window
    /// while keeping the redelivery delay short.
    /// </summary>
    public static readonly TimeSpan DefaultClaimLeaseDuration = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<MessagingDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _claimLeaseDuration;

    /// <summary>
    /// Creates a new outbound queue using <see cref="DefaultClaimLeaseDuration"/>
    /// for the in-flight Sending lease.
    /// </summary>
    public PersistentOutboundQueue(
        IDbContextFactory<MessagingDbContext> contextFactory,
        TimeProvider timeProvider)
        : this(contextFactory, timeProvider, DefaultClaimLeaseDuration)
    {
    }

    /// <summary>
    /// Creates a new outbound queue with an explicit Sending-lease window.
    /// Tests use the explicit overload to drive crash-window recovery
    /// scenarios with a manually advanced clock.
    /// </summary>
    /// <param name="contextFactory">EF Core context factory.</param>
    /// <param name="timeProvider">Clock abstraction.</param>
    /// <param name="claimLeaseDuration">
    /// How long a dispatcher's Sending claim remains exclusive before the
    /// row is considered abandoned and re-eligible for pickup. Must be
    /// strictly positive.
    /// </param>
    public PersistentOutboundQueue(
        IDbContextFactory<MessagingDbContext> contextFactory,
        TimeProvider timeProvider,
        TimeSpan claimLeaseDuration)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        if (claimLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(claimLeaseDuration),
                claimLeaseDuration,
                "claimLeaseDuration must be strictly positive so abandoned Sending claims expire.");
        }

        _claimLeaseDuration = claimLeaseDuration;
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Idempotency contract per IOutboundQueue.EnqueueAsync: a duplicate
        // IdempotencyKey must collapse to the existing row rather than
        // create a second one. We check first (fast path on the unique
        // index) and fall back to a try/catch on SaveChangesAsync to handle
        // the multi-writer race.
        var existing = await context.OutboundMessages
            .AsNoTracking()
            .AnyAsync(x => x.IdempotencyKey == message.IdempotencyKey, ct)
            .ConfigureAwait(false);

        if (existing)
        {
            return;
        }

        context.OutboundMessages.Add(message);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PersistenceConstraintErrors.IsUniqueViolation(ex))
        {
            // Lost the race against a concurrent writer that committed the
            // same IdempotencyKey -- treat as success per the no-op contract.
        }
    }

    /// <inheritdoc />
    public async Task<OutboundMessage?> DequeueAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Severity-ordered pickup: Critical first (Severity int=0), Low last
        // (Severity int=3). Within each severity band, oldest first by
        // CreatedAt. The candidate set is:
        //  - every Pending row (fresh enqueue),
        //  - every Failed row whose NextRetryAt has elapsed (retry due), and
        //  - every Sending row whose NextRetryAt lease has elapsed (crash
        //    recovery per architecture.md §10.3 Gap A -- a dispatcher
        //    crashed mid-send and the lease has expired).
        //
        // SQLite-provider note: EF Core's SQLite provider cannot translate
        // ordering / comparison operators on DateTimeOffset (stored as TEXT),
        // so we pull the candidate band by Status (equality, fully
        // translatable) and resolve NextRetryAt + ordering on the client.
        // The candidate set is bounded by Status filters; production traffic
        // sees this as O(in-flight queue depth), not the full table.
        var candidates = await context.OutboundMessages
            .AsNoTracking()
            .Where(x => x.Status == OutboundMessageStatus.Pending
                        || x.Status == OutboundMessageStatus.Failed
                        || x.Status == OutboundMessageStatus.Sending)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ordered = candidates
            .Where(x => IsEligibleForPickup(x, now))
            .OrderBy(x => x.Severity)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        // Walk candidates in priority order, attempting an atomic claim on
        // each. A concurrent dispatcher that wins the same row first will
        // cause our ExecuteUpdateAsync to report 0 rows affected -- in that
        // case we fall through to the next candidate.
        foreach (var candidate in ordered)
        {
            var claimed = await TryClaimAsync(context, candidate, now, ct).ConfigureAwait(false);
            if (claimed is not null)
            {
                return claimed;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task MarkSentAsync(Guid messageId, long platformMessageId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.OutboundMessages
            .FirstOrDefaultAsync(x => x.MessageId == messageId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        // Records are immutable EF-mapped record types; mutate via property
        // setters which EF's change tracker still picks up because each
        // property has a setter exposed by the positional record's auto-
        // generated init members. EF mutates them through reflection over
        // the backing fields, so this is safe.
        SetStatus(context, record, OutboundMessageStatus.Sent);
        SetPlatformMessageId(context, record, platformMessageId);
        SetSentAt(context, record, _timeProvider.GetUtcNow());
        // Successful delivery clears the dispatcher lease so the recovery
        // sweep cannot re-pick up a row that is already terminally Sent.
        SetNextRetryAt(context, record, null);

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.OutboundMessages
            .FirstOrDefaultAsync(x => x.MessageId == messageId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        var nextAttempt = record.AttemptCount + 1;
        SetAttemptCount(context, record, nextAttempt);
        SetErrorDetail(context, record, error);

        if (nextAttempt >= record.MaxAttempts)
        {
            // Exhausted retries -- transition straight to DeadLettered and
            // create the linked DeadLetterMessage row in the same
            // transaction so the operator triage surface materialises
            // atomically (architecture.md §3.2 1--1 relationship).
            SetStatus(context, record, OutboundMessageStatus.DeadLettered);
            context.DeadLetterMessages.Add(new DeadLetterMessage
            {
                OriginalMessageId = record.MessageId,
                ChatId = record.ChatId,
                Payload = record.Payload,
                ErrorReason = error,
                FailedAt = _timeProvider.GetUtcNow(),
                AttemptCount = nextAttempt,
            });
        }
        else
        {
            SetStatus(context, record, OutboundMessageStatus.Failed);
            SetNextRetryAt(context, record, _timeProvider.GetUtcNow() + ComputeBackoff(nextAttempt));
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeadLetterAsync(Guid messageId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.OutboundMessages
            .FirstOrDefaultAsync(x => x.MessageId == messageId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        var existingDeadLetter = await context.DeadLetterMessages
            .AnyAsync(x => x.OriginalMessageId == record.MessageId, ct)
            .ConfigureAwait(false);

        SetStatus(context, record, OutboundMessageStatus.DeadLettered);

        if (!existingDeadLetter)
        {
            context.DeadLetterMessages.Add(new DeadLetterMessage
            {
                OriginalMessageId = record.MessageId,
                ChatId = record.ChatId,
                Payload = record.Payload,
                ErrorReason = record.ErrorDetail ?? "Forced dead-letter (no failure detail recorded).",
                FailedAt = _timeProvider.GetUtcNow(),
                AttemptCount = Math.Max(record.AttemptCount, 1),
            });
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountPendingAsync(MessageSeverity severity, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await context.OutboundMessages
            .CountAsync(x => x.Severity == severity && x.Status == OutboundMessageStatus.Pending, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboundMessage>> DequeueBatchAsync(
        MessageSeverity severity,
        int maxCount,
        CancellationToken ct)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "maxCount must be strictly positive.");
        }

        var now = _timeProvider.GetUtcNow();

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // See DequeueAsync for the SQLite-provider rationale: status equality
        // is translatable, but NextRetryAt and CreatedAt comparisons are not.
        // We pull the status / severity band server-side and finish the
        // filter + ordering + Take on the client. Each row is claimed via an
        // atomic conditional UPDATE so concurrent dispatchers cannot return
        // the same row twice; race losers fall through to the next candidate
        // and we keep claiming until we hit `maxCount` or run out.
        var candidates = await context.OutboundMessages
            .AsNoTracking()
            .Where(x => x.Severity == severity
                        && (x.Status == OutboundMessageStatus.Pending
                            || x.Status == OutboundMessageStatus.Failed
                            || x.Status == OutboundMessageStatus.Sending))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ordered = candidates
            .Where(x => IsEligibleForPickup(x, now))
            .OrderBy(x => x.CreatedAt)
            .ToList();

        if (ordered.Count == 0)
        {
            return Array.Empty<OutboundMessage>();
        }

        var claimed = new List<OutboundMessage>(Math.Min(ordered.Count, maxCount));
        foreach (var candidate in ordered)
        {
            if (claimed.Count >= maxCount)
            {
                break;
            }

            var winner = await TryClaimAsync(context, candidate, now, ct).ConfigureAwait(false);
            if (winner is not null)
            {
                claimed.Add(winner);
            }
        }

        return claimed;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="candidate"/> is
    /// currently eligible for pickup by a dispatcher.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description><see cref="OutboundMessageStatus.Pending"/> -- always eligible.</description></item>
    ///   <item><description><see cref="OutboundMessageStatus.Failed"/> -- eligible when <c>NextRetryAt</c> has elapsed (retry due).</description></item>
    ///   <item><description><see cref="OutboundMessageStatus.Sending"/> -- eligible when <c>NextRetryAt</c> lease has elapsed (architecture.md §10.3 Gap A: dispatcher crashed mid-send).</description></item>
    /// </list>
    /// </remarks>
    private static bool IsEligibleForPickup(OutboundMessage candidate, DateTimeOffset now)
    {
        return candidate.Status switch
        {
            OutboundMessageStatus.Pending => true,
            OutboundMessageStatus.Failed => candidate.NextRetryAt.HasValue && candidate.NextRetryAt.Value <= now,
            OutboundMessageStatus.Sending => candidate.NextRetryAt.HasValue && candidate.NextRetryAt.Value <= now,
            _ => false,
        };
    }

    /// <summary>
    /// Atomically claims <paramref name="candidate"/> for the calling
    /// dispatcher. The claim flips Status -&gt; Sending and stamps a fresh
    /// <see cref="OutboundMessage.NextRetryAt"/> lease in a single
    /// conditional UPDATE. The WHERE clause guards on BOTH the observed
    /// Status AND the observed <see cref="OutboundMessage.NextRetryAt"/>:
    /// the lease check is essential for the Sending -&gt; Sending
    /// re-claim path because the Status alone does not change between the
    /// observed state and the new state (an expired lease is being
    /// extended), so two dispatchers observing the same expired
    /// <see cref="OutboundMessageStatus.Sending"/> row would otherwise both
    /// succeed. Including <c>NextRetryAt == observedLease</c> in the
    /// predicate gives us optimistic-concurrency semantics: only one
    /// dispatcher wins, the loser sees zero rows affected because the
    /// winner already overwrote the lease.
    /// </summary>
    /// <remarks>
    /// For <see cref="OutboundMessageStatus.Pending"/> rows the lease is
    /// <see langword="null"/>; EF Core 8 translates
    /// <c>x.NextRetryAt == observedNextRetryAt</c> with a null parameter to
    /// the C#-equality SQL pattern
    /// <c>(x.NextRetryAt IS NULL AND @observed IS NULL) OR x.NextRetryAt = @observed</c>,
    /// which collapses to <c>x.NextRetryAt IS NULL</c> when the parameter is
    /// null. The Pending -&gt; Sending transition is therefore still
    /// race-safe via the Status flip alone (Status changes from Pending to
    /// Sending, so the second dispatcher's WHERE matches zero rows); the
    /// lease check is the redundant-but-correct optimistic token.
    /// </remarks>
    private async Task<OutboundMessage?> TryClaimAsync(
        MessagingDbContext context,
        OutboundMessage candidate,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var observedStatus = candidate.Status;
        var observedNextRetryAt = candidate.NextRetryAt;
        var leaseUntil = now + _claimLeaseDuration;

        // Conditional UPDATE -- the WHERE Status == observed AND
        // NextRetryAt == observed clause is what makes the claim race-safe
        // under concurrent dispatchers, including the Sending -> Sending
        // expired-lease reclaim case. SQLite translates this into a single
        // statement; rowsAffected is 1 when we won and 0 when another
        // dispatcher has already overwritten the row's Status or lease.
        var rowsAffected = await context.OutboundMessages
            .Where(x => x.MessageId == candidate.MessageId
                        && x.Status == observedStatus
                        && x.NextRetryAt == observedNextRetryAt)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, OutboundMessageStatus.Sending)
                    .SetProperty(x => x.NextRetryAt, (DateTimeOffset?)leaseUntil),
                ct)
            .ConfigureAwait(false);

        if (rowsAffected == 0)
        {
            return null;
        }

        // Reload the row so the caller sees the post-claim Status + lease
        // values without us having to mutate the AsNoTracking snapshot.
        return await context.OutboundMessages
            .AsNoTracking()
            .FirstAsync(x => x.MessageId == candidate.MessageId, ct)
            .ConfigureAwait(false);
    }

    private static void SetStatus(MessagingDbContext context, OutboundMessage record, OutboundMessageStatus value)
    {
        context.Entry(record).Property(nameof(OutboundMessage.Status)).CurrentValue = value;
    }

    private static void SetPlatformMessageId(MessagingDbContext context, OutboundMessage record, long? value)
    {
        context.Entry(record).Property(nameof(OutboundMessage.PlatformMessageId)).CurrentValue = value;
    }

    private static void SetSentAt(MessagingDbContext context, OutboundMessage record, DateTimeOffset? value)
    {
        context.Entry(record).Property(nameof(OutboundMessage.SentAt)).CurrentValue = value;
    }

    private static void SetAttemptCount(MessagingDbContext context, OutboundMessage record, int value)
    {
        context.Entry(record).Property(nameof(OutboundMessage.AttemptCount)).CurrentValue = value;
    }

    private static void SetErrorDetail(MessagingDbContext context, OutboundMessage record, string? value)
    {
        context.Entry(record).Property(nameof(OutboundMessage.ErrorDetail)).CurrentValue = value;
    }

    private static void SetNextRetryAt(MessagingDbContext context, OutboundMessage record, DateTimeOffset? value)
    {
        context.Entry(record).Property(nameof(OutboundMessage.NextRetryAt)).CurrentValue = value;
    }

    /// <summary>
    /// Capped exponential backoff schedule: 1s, 2s, 4s, 8s, 16s for attempts
    /// 1..5. Stage 2.3 may refine the schedule (jitter, per-platform caps);
    /// this baseline keeps retries from hammering Discord's rate-limit
    /// buckets while preserving fast recovery from transient failures.
    /// </summary>
    private static TimeSpan ComputeBackoff(int attemptCount)
    {
        if (attemptCount < 1)
        {
            attemptCount = 1;
        }

        // 2^(attempt-1) seconds, capped at 60s so a misconfigured MaxAttempts
        // does not leave a row asleep for hours.
        var seconds = Math.Min(60d, Math.Pow(2, attemptCount - 1));
        return TimeSpan.FromSeconds(seconds);
    }
}
