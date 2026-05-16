using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core-backed implementation of <see cref="IPendingQuestionStore"/>.
/// Persists the wrapped <see cref="AgentQuestion"/> as JSON and the Discord
/// routing snowflakes as native <c>ulong</c> columns (architecture.md §3.1
/// PendingQuestionRecord). Returns the platform-neutral
/// <see cref="PendingQuestion"/> DTO with snowflakes reinterpreted to
/// <see cref="long"/> per the contract on
/// <see cref="IPendingQuestionStore.StoreAsync"/>.
/// </summary>
public sealed class PersistentPendingQuestionStore : IPendingQuestionStore
{
    private const string RoutingMetadataThreadKey = "DiscordThreadId";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IDbContextFactory<MessagingDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a new pending-question store.</summary>
    public PersistentPendingQuestionStore(
        IDbContextFactory<MessagingDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task StoreAsync(
        AgentQuestionEnvelope envelope,
        long channelId,
        long platformMessageId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var question = envelope.Question;
        var defaultAction = ResolveDefaultAction(envelope);
        var threadId = ExtractThreadId(envelope);

        var record = new PendingQuestionRecord
        {
            QuestionId = question.QuestionId,
            AgentQuestion = JsonSerializer.Serialize(question, JsonOptions),
            DiscordChannelId = (ulong)channelId,
            DiscordMessageId = (ulong)platformMessageId,
            DiscordThreadId = threadId,
            DefaultActionId = defaultAction?.ActionId,
            DefaultActionValue = defaultAction?.Value,
            ExpiresAt = question.ExpiresAt,
            Status = PendingQuestionStatus.Pending,
            SelectedActionId = null,
            SelectedActionValue = null,
            RespondentUserId = null,
            StoredAt = _timeProvider.GetUtcNow(),
            CorrelationId = question.CorrelationId,
        };

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Idempotency contract: a PendingQuestionRecord is keyed by
        // QuestionId, and the same logical question may legitimately be
        // re-presented by two different code paths during crash recovery:
        //   - the producer's at-most-once attempt that originally posted
        //     the Discord message, and
        //   - the QuestionRecoverySweep's Gap B backfill (architecture.md
        //     §10.3) that reconstructs the row from the OutboundMessage
        //     SourceEnvelopeJson after a crash between Discord-side
        //     success and PendingQuestionRecord persistence.
        // Either path landing first must leave a single row -- and the
        // late path must be a benign no-op rather than a thrown
        // exception. This is the operational guarantee that fan-out
        // duplicates collapse silently.
        var existing = await context.PendingQuestions
            .AsNoTracking()
            .AnyAsync(x => x.QuestionId == question.QuestionId, ct)
            .ConfigureAwait(false);

        if (existing)
        {
            return;
        }

        context.PendingQuestions.Add(record);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PersistenceConstraintErrors.IsUniqueViolation(ex))
        {
            // Lost the race against a concurrent writer that committed the
            // same QuestionId between the existence probe and our save --
            // collapse to no-op per the contract above.
        }
    }

    /// <inheritdoc />
    public async Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.PendingQuestions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        return record is null ? null : MapToDto(record);
    }

    /// <inheritdoc />
    public Task MarkAnsweredAsync(string questionId, CancellationToken ct)
        => UpdateStatusAsync(questionId, PendingQuestionStatus.Answered, ct);

    /// <inheritdoc />
    public Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct)
        => UpdateStatusAsync(questionId, PendingQuestionStatus.AwaitingComment, ct);

    /// <inheritdoc />
    public async Task RecordSelectionAsync(
        string questionId,
        string selectedActionId,
        string selectedActionValue,
        long respondentUserId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedActionId);
        ArgumentNullException.ThrowIfNull(selectedActionValue);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.PendingQuestions
            .FirstOrDefaultAsync(x => x.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        record.SelectedActionId = selectedActionId;
        record.SelectedActionValue = selectedActionValue;
        record.RespondentUserId = (ulong)respondentUserId;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Per IPendingQuestionStore.GetExpiredAsync: every question whose
        // ExpiresAt has elapsed and that is still in Pending or
        // AwaitingComment. Ordering by ExpiresAt keeps the timeout
        // processor deterministic.
        //
        // SQLite-provider note: EF Core's SQLite provider does not translate
        // comparison operators on DateTimeOffset (the column is stored as
        // TEXT). We pull live rows by Status (equality, fully translatable)
        // and apply the ExpiresAt cutoff + ordering on the client. The live
        // set is bounded by the question TTL window and stays small.
        var liveRecords = await context.PendingQuestions
            .AsNoTracking()
            .Where(x => x.Status == PendingQuestionStatus.Pending
                        || x.Status == PendingQuestionStatus.AwaitingComment)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var expired = liveRecords
            .Where(x => x.ExpiresAt <= now)
            .OrderBy(x => x.ExpiresAt)
            .Select(MapToDto)
            .ToArray();

        return expired;
    }

    private async Task UpdateStatusAsync(
        string questionId,
        PendingQuestionStatus targetStatus,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.PendingQuestions
            .FirstOrDefaultAsync(x => x.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        // Idempotent in the targeted terminal state -- the contract on
        // MarkAnsweredAsync explicitly calls this out.
        if (record.Status == targetStatus)
        {
            return;
        }

        record.Status = targetStatus;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static HumanAction? ResolveDefaultAction(AgentQuestionEnvelope envelope)
    {
        if (string.IsNullOrEmpty(envelope.ProposedDefaultActionId))
        {
            return null;
        }

        foreach (var action in envelope.Question.AllowedActions)
        {
            if (string.Equals(action.ActionId, envelope.ProposedDefaultActionId, StringComparison.Ordinal))
            {
                return action;
            }
        }

        return null;
    }

    private static ulong? ExtractThreadId(AgentQuestionEnvelope envelope)
    {
        if (!envelope.RoutingMetadata.TryGetValue(RoutingMetadataThreadKey, out var raw))
        {
            // Key absent: caller declined to thread the message. Null-thread
            // routing is a first-class option for question messages posted
            // directly into a control channel.
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Key present but blank: same semantic as absent (the connector
            // upstream may emit a key with an empty value to explicitly
            // unset a previously-attached thread). Treat as no-thread.
            return null;
        }

        if (!ulong.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            // Key present with malformed data: this is a connector contract
            // violation (a Discord thread snowflake must be an unsigned
            // 64-bit integer). Surfacing it as FormatException makes the
            // bad payload visible at the storage boundary rather than
            // silently degrading to no-thread routing -- which would
            // mis-post the question into the control channel root.
            throw new FormatException(
                $"AgentQuestionEnvelope routing metadata '{RoutingMetadataThreadKey}' must be a Discord thread " +
                $"snowflake (unsigned 64-bit integer). Received: '{raw}'.");
        }

        return parsed;
    }

    private static PendingQuestion MapToDto(PendingQuestionRecord record)
    {
        var question = JsonSerializer.Deserialize<AgentQuestion>(record.AgentQuestion, JsonOptions)
            ?? throw new InvalidOperationException(
                $"PendingQuestionRecord '{record.QuestionId}' has malformed AgentQuestion JSON.");

        return new PendingQuestion(
            QuestionId: record.QuestionId,
            Question: question,
            ChannelId: (long)record.DiscordChannelId,
            PlatformMessageId: (long)record.DiscordMessageId,
            ThreadId: record.DiscordThreadId.HasValue ? (long)record.DiscordThreadId.Value : null,
            DefaultActionId: record.DefaultActionId,
            DefaultActionValue: record.DefaultActionValue,
            ExpiresAt: record.ExpiresAt,
            Status: record.Status,
            SelectedActionId: record.SelectedActionId,
            SelectedActionValue: record.SelectedActionValue,
            RespondentUserId: record.RespondentUserId.HasValue ? (long)record.RespondentUserId.Value : null,
            StoredAt: record.StoredAt,
            CorrelationId: record.CorrelationId);
    }
}
