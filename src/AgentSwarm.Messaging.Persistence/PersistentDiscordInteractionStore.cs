using AgentSwarm.Messaging.Discord;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core backed implementation of <see cref="IDiscordInteractionStore"/>.
/// Lives in the Persistence assembly per implementation-plan Stage 2.2 line
/// 106 ("Implement `PersistentDiscordInteractionStore` class in Persistence
/// backed by `MessagingDbContext`"). The
/// <c>IX_DiscordInteractions_InteractionId_Unique</c> constraint is the
/// canonical cross-restart dedup mechanism: <see cref="PersistAsync"/>
/// catches the resulting <see cref="DbUpdateException"/> and returns
/// <see langword="false"/> rather than propagating, so Discord webhook
/// retries that produce duplicate snowflakes collapse into a single inbox
/// row.
/// </summary>
public sealed class PersistentDiscordInteractionStore : IDiscordInteractionStore
{
    /// <summary>
    /// Default staleness window for <see cref="GetRecoverableAsync"/>. A
    /// row whose most recent activity is younger than this threshold is
    /// treated as potentially in-flight on a sibling dispatcher and is
    /// excluded from the recovery batch. Five minutes comfortably covers
    /// the worst-case interaction-pipeline latency while keeping a crashed
    /// host's abandoned rows recoverable on the next sweep tick.
    /// </summary>
    public static readonly TimeSpan DefaultRecoveryStaleAfter = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<MessagingDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _recoveryStaleAfter;

    /// <summary>
    /// Creates a new store using <see cref="DefaultRecoveryStaleAfter"/> for
    /// the internal recovery staleness window.
    /// </summary>
    /// <param name="contextFactory">Factory for <see cref="MessagingDbContext"/>.</param>
    /// <param name="timeProvider">
    /// Clock abstraction so unit tests can pin "now".
    /// </param>
    public PersistentDiscordInteractionStore(
        IDbContextFactory<MessagingDbContext> contextFactory,
        TimeProvider timeProvider)
        : this(contextFactory, timeProvider, DefaultRecoveryStaleAfter)
    {
    }

    /// <summary>
    /// Creates a new store with an explicit recovery staleness window. Tests
    /// drive deterministic recovery scenarios through this overload by
    /// pairing a manually-advanced clock with a tightly-bounded threshold.
    /// </summary>
    /// <param name="contextFactory">Factory for <see cref="MessagingDbContext"/>.</param>
    /// <param name="timeProvider">Clock abstraction.</param>
    /// <param name="recoveryStaleAfter">
    /// How long after the last activity a row must sit untouched before
    /// <see cref="GetRecoverableAsync"/> treats it as recoverable. Must be
    /// strictly positive.
    /// </param>
    public PersistentDiscordInteractionStore(
        IDbContextFactory<MessagingDbContext> contextFactory,
        TimeProvider timeProvider,
        TimeSpan recoveryStaleAfter)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        if (recoveryStaleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryStaleAfter),
                recoveryStaleAfter,
                "recoveryStaleAfter must be strictly positive so the recovery sweep cannot snatch active in-flight rows.");
        }

        _recoveryStaleAfter = recoveryStaleAfter;
    }

    /// <inheritdoc />
    public async Task<bool> PersistAsync(DiscordInteractionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Default the timestamp at the storage boundary so callers that just
        // wire up the snowflake fields do not need to remember it. We do not
        // overwrite a non default ReceivedAt because the recovery sweep
        // re persists existing rows verbatim.
        if (record.ReceivedAt == default)
        {
            record.ReceivedAt = _timeProvider.GetUtcNow();
        }

        context.DiscordInteractions.Add(record);

        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (PersistenceConstraintErrors.IsUniqueViolation(ex))
        {
            // Duplicate webhook replay (architecture.md §10.2): swallow and
            // signal the caller to skip processing.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task MarkProcessingAsync(ulong interactionId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.DiscordInteractions
            .FirstOrDefaultAsync(x => x.InteractionId == interactionId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        if (record.IdempotencyStatus == IdempotencyStatus.Completed)
        {
            // Terminal state: never reopen.
            return;
        }

        record.IdempotencyStatus = IdempotencyStatus.Processing;
        record.AttemptCount += 1;
        // Stamp ProcessedAt to "claim activity now". The recovery sweep uses
        // this timestamp as the lease anchor so a sibling dispatcher's
        // sweep cannot snatch the row away while it is being processed by us.
        record.ProcessedAt = _timeProvider.GetUtcNow();
        // Clear any prior failure detail so retries do not leak the previous
        // attempt's error text into a successful completion.
        record.ErrorDetail = null;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(ulong interactionId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.DiscordInteractions
            .FirstOrDefaultAsync(x => x.InteractionId == interactionId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        record.IdempotencyStatus = IdempotencyStatus.Completed;
        record.ProcessedAt = _timeProvider.GetUtcNow();
        record.ErrorDetail = null;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(ulong interactionId, string errorDetail, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorDetail);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.DiscordInteractions
            .FirstOrDefaultAsync(x => x.InteractionId == interactionId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        record.IdempotencyStatus = IdempotencyStatus.Failed;
        record.ProcessedAt = _timeProvider.GetUtcNow();
        record.ErrorDetail = errorDetail;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscordInteractionRecord>> GetRecoverableAsync(
        int maxRetries,
        CancellationToken ct)
    {
        if (maxRetries <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRetries),
                maxRetries,
                "maxRetries must be strictly positive.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Eligible: every non-Completed row that still has retry budget left.
        //
        // SQLite provider note: EF Core's SQLite provider rejects both ORDER
        // BY and comparison operators on DateTimeOffset columns (the column
        // is stored as TEXT). We narrow server-side via the translatable
        // predicates (Status + AttemptCount), then apply the staleness
        // window (via the ctor-injected _recoveryStaleAfter) and the
        // ordering on the client. The recoverable window is bounded by the
        // operator's retry budget so the materialised set is small.
        var rows = await context.DiscordInteractions
            .AsNoTracking()
            .Where(x => x.IdempotencyStatus != IdempotencyStatus.Completed
                        && x.AttemptCount < maxRetries)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var cutoff = now - _recoveryStaleAfter;

        return rows
            .Where(x => LastActivityAt(x) <= cutoff)
            .OrderBy(LastActivityAt)
            .ToArray();
    }

    /// <summary>
    /// Most recent point at which any dispatcher touched the row.
    /// <see cref="DiscordInteractionRecord.ProcessedAt"/> is updated by every
    /// MarkProcessing / MarkCompleted / MarkFailed transition, so it stands
    /// in as the row's lease anchor. Rows that were inserted but never
    /// claimed (<see cref="IdempotencyStatus.Received"/> with
    /// <c>ProcessedAt == null</c>) fall back to
    /// <see cref="DiscordInteractionRecord.ReceivedAt"/>.
    /// </summary>
    private static DateTimeOffset LastActivityAt(DiscordInteractionRecord record)
        => record.ProcessedAt ?? record.ReceivedAt;
}
