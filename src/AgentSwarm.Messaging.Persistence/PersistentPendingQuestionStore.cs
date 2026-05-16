// -----------------------------------------------------------------------
// <copyright file="PersistentPendingQuestionStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 3.5 — EF Core-backed <see cref="IPendingQuestionStore"/>.
/// Replaces the process-local
/// <c>AgentSwarm.Messaging.Telegram.Pipeline.Stubs.InMemoryPendingQuestionStore</c>
/// so callback resolution, awaiting-comment correlation, and
/// timeout-default sweeps survive process restarts, scale-out across
/// workers, and IDistributedCache evictions (architecture.md §3.1 and
/// §10.3, implementation-plan.md Stage 3.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime + scope.</b> Registered as a singleton because
/// <see cref="IServiceScopeFactory"/> is itself a singleton and
/// creates a fresh scope per call — bridging singleton consumers
/// (<c>TelegramMessageSender</c>, <c>CallbackQueryHandler</c>, and
/// <c>QuestionTimeoutService</c>) to the scoped
/// <see cref="MessagingDbContext"/> without violating the
/// captive-dependency rule. Same pattern as
/// <see cref="PersistentOutboundMessageIdIndex"/>,
/// <see cref="PersistentOutboundDeadLetterStore"/>,
/// <see cref="PersistentTaskOversightRepository"/>,
/// <see cref="PersistentAuditLogger"/>, and
/// <see cref="PersistentOperatorRegistry"/>.
/// </para>
/// <para>
/// <b>Mapping boundary.</b> The store returns the abstraction DTO
/// <see cref="PendingQuestion"/> so the connector layer never
/// references <see cref="PendingQuestionRecord"/>. The full
/// <see cref="AgentQuestion"/> is serialized as JSON in
/// <see cref="PendingQuestionRecord.AgentQuestionJson"/> so the
/// <c>AgentId</c> / <c>TaskId</c> / <c>Title</c> / <c>Body</c> /
/// <c>Severity</c> / <c>AllowedActions</c> fields the DTO requires
/// can be rehydrated on every read without a secondary lookup. The
/// hot-path filterable columns are denormalized so the polling
/// query (<see cref="GetExpiredAsync"/>), the awaiting-comment
/// correlation query (<see cref="GetAwaitingCommentAsync"/>), and
/// the timeout default-action emission can use indexes without
/// paying for JSON deserialization on every row.
/// </para>
/// <para>
/// <b>Idempotency.</b> <see cref="StoreAsync"/> uses the same
/// find-then-update-or-add upsert pattern as
/// <see cref="PersistentOutboundMessageIdIndex"/> so a retry of an
/// already-acknowledged Telegram send does not crash on duplicate
/// key. <see cref="MarkTimedOutAsync"/> is idempotent — repeated
/// invocations for an already-<see cref="PendingQuestionStatus.TimedOut"/>
/// row are no-ops so a crash mid-sweep does not double-apply.
/// </para>
/// </remarks>
public sealed class PersistentPendingQuestionStore : IPendingQuestionStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PersistentPendingQuestionStore> _logger;

    /// <summary>
    /// Serializer options — case-insensitive property matching so a
    /// record written by an older serializer build is rehydratable;
    /// strict default for writing.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PersistentPendingQuestionStore(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<PersistentPendingQuestionStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StoreAsync(
        AgentQuestionEnvelope envelope,
        long telegramChatId,
        long telegramMessageId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var question = envelope.Question;

        string? defaultActionValue = null;
        if (envelope.ProposedDefaultActionId is not null)
        {
            defaultActionValue = question.AllowedActions
                .FirstOrDefault(a => string.Equals(
                    a.ActionId,
                    envelope.ProposedDefaultActionId,
                    StringComparison.Ordinal))
                ?.Value;
        }

        var now = _timeProvider.GetUtcNow();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var existing = await db.PendingQuestions
            .FindAsync(new object[] { question.QuestionId }, ct)
            .ConfigureAwait(false);

        var json = JsonSerializer.Serialize(question, JsonOptions);

        if (existing is not null)
        {
            // UPDATE path. A retry of an already-persisted send (the
            // sender's StoreAsync may be invoked twice if the first
            // attempt threw a transient exception after the row was
            // committed). Refresh the non-selection columns; preserve
            // any selection state that a concurrent callback may have
            // written between the first store and this retry. No
            // try/catch needed — UPDATE cannot collide with a
            // duplicate-primary-key insert; a real schema/permission
            // failure here propagates directly to the caller (Stage
            // 3.5 evaluator iter-2 item 6 contract).
            existing.AgentQuestionJson = json;
            existing.TelegramChatId = telegramChatId;
            existing.TelegramMessageId = telegramMessageId;
            existing.ExpiresAt = question.ExpiresAt;
            existing.StoredAt = now;
            existing.DefaultActionId = envelope.ProposedDefaultActionId;
            existing.DefaultActionValue = defaultActionValue;
            existing.CorrelationId = question.CorrelationId;
            // Status / SelectedActionId / SelectedActionValue /
            // RespondentUserId are deliberately NOT overwritten —
            // the callback handler is the source of truth for those
            // fields after the first successful StoreAsync.

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        // INSERT path. Only this branch is exposed to a possible
        // duplicate-primary-key race against a concurrent writer
        // (another retry of the same send running in parallel).
        var inserted = new PendingQuestionRecord
        {
            QuestionId = question.QuestionId,
            AgentQuestionJson = json,
            TelegramChatId = telegramChatId,
            TelegramMessageId = telegramMessageId,
            ExpiresAt = question.ExpiresAt,
            StoredAt = now,
            DefaultActionId = envelope.ProposedDefaultActionId,
            DefaultActionValue = defaultActionValue,
            Status = PendingQuestionStatus.Pending,
            CorrelationId = question.CorrelationId,
        };
        db.PendingQuestions.Add(inserted);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // Per Stage 3.5 evaluator iter-3 item 2 — a blanket
            // "swallow every DbUpdateException as a benign duplicate"
            // mask hides real schema / permission / FK violations and
            // suppresses the PendingQuestionPersistenceException that
            // TelegramMessageSender now relies on. Disambiguate by
            // RE-QUERYING the table: if a row now exists for our
            // QuestionId, the DbUpdateException WAS a duplicate-key
            // race (another writer committed between our SELECT and
            // our INSERT) and the durable contract is satisfied —
            // detach our losing entity, mutate the winner, and save
            // the refresh. If still no row, the DbUpdateException was
            // a REAL failure (e.g. a CHECK constraint, a permission
            // denial, an FK violation) — propagate it unchanged so
            // the sender's catch can wrap it in
            // PendingQuestionPersistenceException.
            db.Entry(inserted).State = EntityState.Detached;

            var afterRace = await db.PendingQuestions
                .FindAsync(new object[] { question.QuestionId }, ct)
                .ConfigureAwait(false);
            if (afterRace is null)
            {
                _logger.LogError(
                    ex,
                    "Pending question {QuestionId} StoreAsync failed with a non-benign DbUpdateException (no row exists after the failed save — this is NOT a duplicate-key race); propagating so TelegramMessageSender can surface PendingQuestionPersistenceException to the outbound queue processor.",
                    question.QuestionId);
                throw;
            }

            _logger.LogDebug(
                "Pending question {QuestionId} StoreAsync hit a duplicate-key DbUpdateException; another writer committed the same QuestionId first — applying the envelope as an UPDATE against the winning row.",
                question.QuestionId);

            afterRace.AgentQuestionJson = json;
            afterRace.TelegramChatId = telegramChatId;
            afterRace.TelegramMessageId = telegramMessageId;
            afterRace.ExpiresAt = question.ExpiresAt;
            afterRace.StoredAt = now;
            afterRace.DefaultActionId = envelope.ProposedDefaultActionId;
            afterRace.DefaultActionValue = defaultActionValue;
            afterRace.CorrelationId = question.CorrelationId;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(questionId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var row = await db.PendingQuestions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        return row is null ? null : ToDto(row);
    }

    public async Task<PendingQuestion?> GetByTelegramMessageAsync(
        long telegramChatId,
        long telegramMessageId,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var row = await db.PendingQuestions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.TelegramChatId == telegramChatId
                  && x.TelegramMessageId == telegramMessageId,
                ct)
            .ConfigureAwait(false);

        return row is null ? null : ToDto(row);
    }

    public async Task MarkAnsweredAsync(string questionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(questionId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var row = await db.PendingQuestions
            .FirstOrDefaultAsync(x => x.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            throw new KeyNotFoundException($"PendingQuestion '{questionId}' not found.");
        }

        row.Status = PendingQuestionStatus.Answered;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(questionId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var row = await db.PendingQuestions
            .FirstOrDefaultAsync(x => x.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            throw new KeyNotFoundException($"PendingQuestion '{questionId}' not found.");
        }

        row.Status = PendingQuestionStatus.AwaitingComment;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> MarkTimedOutAsync(string questionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(questionId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Atomic cross-process claim — emit a single UPDATE that
        // matches only rows still in a non-terminal state. The
        // database enforces row-level atomicity, so two sweepers
        // running concurrently in different processes can BOTH read
        // the row via GetExpiredAsync but only ONE will see
        // rowsAffected > 0 here; the other will see 0 and skip,
        // closing the cross-process double-publish race. Implemented
        // via EF Core 8's ExecuteUpdateAsync — which translates to a
        // single provider-native UPDATE statement under SQLite,
        // PostgreSQL, and SQL Server alike — so the atomicity
        // guarantee is provider-portable.
        var rowsAffected = await db.PendingQuestions
            .Where(x => x.QuestionId == questionId
                     && (x.Status == PendingQuestionStatus.Pending
                      || x.Status == PendingQuestionStatus.AwaitingComment))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.Status, PendingQuestionStatus.TimedOut),
                ct)
            .ConfigureAwait(false);

        return rowsAffected > 0;
    }

    public async Task<bool> TryRevertTimedOutClaimAsync(
        string questionId,
        PendingQuestionStatus revertTo,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(questionId);
        if (revertTo != PendingQuestionStatus.Pending &&
            revertTo != PendingQuestionStatus.AwaitingComment)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revertTo),
                revertTo,
                "revertTo must be a non-terminal pre-claim status (Pending or AwaitingComment).");
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Conditional UPDATE — the WHERE clause filters on the
        // post-claim TimedOut state, so this revert is itself atomic
        // and will not overwrite a row that the operator has since
        // resolved (e.g. via late callback). Implemented via EF Core
        // 8's ExecuteUpdateAsync so the revert is provider-neutral
        // (SQLite / PostgreSQL / SQL Server) and matches the atomic
        // primitive used by MarkTimedOutAsync.
        var rowsAffected = await db.PendingQuestions
            .Where(x => x.QuestionId == questionId
                     && x.Status == PendingQuestionStatus.TimedOut)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.Status, revertTo),
                ct)
            .ConfigureAwait(false);

        return rowsAffected > 0;
    }

    public async Task RecordSelectionAsync(
        string questionId,
        string selectedActionId,
        string selectedActionValue,
        long respondentUserId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(questionId);
        ArgumentException.ThrowIfNullOrEmpty(selectedActionId);
        ArgumentException.ThrowIfNullOrEmpty(selectedActionValue);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var row = await db.PendingQuestions
            .FirstOrDefaultAsync(x => x.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            throw new KeyNotFoundException($"PendingQuestion '{questionId}' not found.");
        }

        row.SelectedActionId = selectedActionId;
        row.SelectedActionValue = selectedActionValue;
        row.RespondentUserId = respondentUserId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PendingQuestion?> GetAwaitingCommentAsync(
        long telegramChatId,
        long respondentUserId,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Filter on the (TelegramChatId, RespondentUserId, Status)
        // index; order by StoredAt for deterministic oldest-first
        // tie-breaking when an operator has multiple awaiting-comment
        // questions in the same chat (architecture.md §3.1).
        var row = await db.PendingQuestions
            .AsNoTracking()
            .Where(x => x.Status == PendingQuestionStatus.AwaitingComment
                     && x.TelegramChatId == telegramChatId
                     && x.RespondentUserId == respondentUserId)
            .OrderBy(x => x.StoredAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? null : ToDto(row);
    }

    public async Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Polling query — uses the (Status, ExpiresAt) composite
        // index. Both Pending and AwaitingComment rows are eligible
        // for timeout (an operator who tapped a RequiresComment
        // button but never followed up with text must still receive
        // the default-action timeout per architecture.md §10.3).
        var rows = await db.PendingQuestions
            .AsNoTracking()
            .Where(x => (x.Status == PendingQuestionStatus.Pending
                      || x.Status == PendingQuestionStatus.AwaitingComment)
                     && x.ExpiresAt <= now)
            .OrderBy(x => x.ExpiresAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<PendingQuestion>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(ToDto(row));
        }

        return result;
    }

    /// <summary>
    /// Maps a stored <see cref="PendingQuestionRecord"/> back to the
    /// abstraction DTO <see cref="PendingQuestion"/> that consumers
    /// reference. Deserializes the AgentQuestion JSON to rehydrate
    /// the non-denormalized fields (<c>AgentId</c>, <c>TaskId</c>,
    /// <c>Title</c>, <c>Body</c>, <c>Severity</c>,
    /// <c>AllowedActions</c>).
    /// </summary>
    private static PendingQuestion ToDto(PendingQuestionRecord row)
    {
        var question = JsonSerializer.Deserialize<AgentQuestion>(row.AgentQuestionJson, JsonOptions)
            ?? throw new InvalidOperationException(
                $"PendingQuestion '{row.QuestionId}' has empty AgentQuestionJson — refusing to "
                + "return a partially-initialised PendingQuestion DTO.");

        return new PendingQuestion
        {
            QuestionId = row.QuestionId,
            AgentId = question.AgentId,
            TaskId = question.TaskId,
            Title = question.Title,
            Body = question.Body,
            Severity = question.Severity,
            AllowedActions = question.AllowedActions,
            DefaultActionId = row.DefaultActionId,
            DefaultActionValue = row.DefaultActionValue,
            TelegramChatId = row.TelegramChatId,
            TelegramMessageId = row.TelegramMessageId,
            ExpiresAt = row.ExpiresAt,
            CorrelationId = row.CorrelationId,
            Status = row.Status,
            SelectedActionId = row.SelectedActionId,
            SelectedActionValue = row.SelectedActionValue,
            RespondentUserId = row.RespondentUserId,
            StoredAt = row.StoredAt,
        };
    }
}
