using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline.Stubs;

/// <summary>
/// Stage 2.2 in-memory stub <see cref="IPendingQuestionStore"/>. Holds
/// pending questions in a process-local <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// so the pipeline can resolve awaiting-comment text replies before
/// Stage 3.5 ships the EF-backed <c>PendingQuestionStore</c>.
/// </summary>
/// <remarks>
/// State is lost on restart and not shared across processes; the
/// persistent replacement in Stage 3.5 closes both gaps.
/// </remarks>
internal sealed class InMemoryPendingQuestionStore : IPendingQuestionStore
{
    private readonly ConcurrentDictionary<string, PendingQuestion> _byQuestionId =
        new(StringComparer.Ordinal);

    public Task StoreAsync(
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
                .FirstOrDefault(a => string.Equals(a.ActionId, envelope.ProposedDefaultActionId, StringComparison.Ordinal))
                ?.Value;
        }

        var record = new PendingQuestion
        {
            QuestionId = question.QuestionId,
            AgentId = question.AgentId,
            TaskId = question.TaskId,
            Title = question.Title,
            Body = question.Body,
            Severity = question.Severity,
            AllowedActions = question.AllowedActions,
            DefaultActionId = envelope.ProposedDefaultActionId,
            DefaultActionValue = defaultActionValue,
            TelegramChatId = telegramChatId,
            TelegramMessageId = telegramMessageId,
            ExpiresAt = question.ExpiresAt,
            CorrelationId = question.CorrelationId,
            Status = PendingQuestionStatus.Pending,
            StoredAt = DateTimeOffset.UtcNow,
        };

        _byQuestionId[question.QuestionId] = record;
        return Task.CompletedTask;
    }

    public Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct)
    {
        _byQuestionId.TryGetValue(questionId, out var record);
        return Task.FromResult<PendingQuestion?>(record);
    }

    public Task<PendingQuestion?> GetByTelegramMessageAsync(
        long telegramChatId,
        long telegramMessageId,
        CancellationToken ct)
    {
        var record = _byQuestionId.Values
            .FirstOrDefault(r => r.TelegramChatId == telegramChatId && r.TelegramMessageId == telegramMessageId);
        return Task.FromResult<PendingQuestion?>(record);
    }

    public Task MarkAnsweredAsync(string questionId, CancellationToken ct)
    {
        Mutate(questionId, current => current with { Status = PendingQuestionStatus.Answered });
        return Task.CompletedTask;
    }

    public Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct)
    {
        Mutate(questionId, current => current with { Status = PendingQuestionStatus.AwaitingComment });
        return Task.CompletedTask;
    }

    public Task<bool> MarkTimedOutAsync(string questionId, CancellationToken ct)
    {
        // Atomic claim via ConcurrentDictionary.TryUpdate — the
        // (key, newValue, comparisonValue) overload performs a
        // compare-and-swap, succeeding only when the current value
        // still equals comparisonValue. Two concurrent sweepers
        // both calling MarkTimedOutAsync therefore see exactly ONE
        // true result; the loser sees false. Mirrors the
        // ExecuteUpdateAsync-based atomic claim in
        // PersistentPendingQuestionStore.MarkTimedOutAsync.
        if (!_byQuestionId.TryGetValue(questionId, out var current))
        {
            return Task.FromResult(false);
        }

        if (current.Status != PendingQuestionStatus.Pending &&
            current.Status != PendingQuestionStatus.AwaitingComment)
        {
            // Already terminal (TimedOut or Answered) — another
            // sweeper / a callback won the claim before us.
            return Task.FromResult(false);
        }

        var updated = current with { Status = PendingQuestionStatus.TimedOut };
        var claimed = _byQuestionId.TryUpdate(questionId, updated, current);
        return Task.FromResult(claimed);
    }

    public Task<bool> TryRevertTimedOutClaimAsync(
        string questionId,
        PendingQuestionStatus revertTo,
        CancellationToken ct)
    {
        if (revertTo != PendingQuestionStatus.Pending &&
            revertTo != PendingQuestionStatus.AwaitingComment)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revertTo),
                revertTo,
                "revertTo must be a non-terminal pre-claim status (Pending or AwaitingComment).");
        }

        // Compare-and-swap mirror of the EF
        // ExecuteUpdateAsync(WHERE Status == TimedOut) — only revert
        // when the row is still in the TimedOut state we claimed.
        // Two concurrent reverts therefore see exactly ONE true; the
        // loser sees false and skips. Mirrors the atomic-claim CAS
        // semantics of MarkTimedOutAsync above.
        if (!_byQuestionId.TryGetValue(questionId, out var current))
        {
            return Task.FromResult(false);
        }

        if (current.Status != PendingQuestionStatus.TimedOut)
        {
            return Task.FromResult(false);
        }

        var reverted = current with { Status = revertTo };
        var success = _byQuestionId.TryUpdate(questionId, reverted, current);
        return Task.FromResult(success);
    }

    public Task RecordSelectionAsync(
        string questionId,
        string selectedActionId,
        string selectedActionValue,
        long respondentUserId,
        CancellationToken ct)
    {
        Mutate(questionId, current => current with
        {
            SelectedActionId = selectedActionId,
            SelectedActionValue = selectedActionValue,
            RespondentUserId = respondentUserId,
        });
        return Task.CompletedTask;
    }

    public Task<PendingQuestion?> GetAwaitingCommentAsync(
        long telegramChatId,
        long respondentUserId,
        CancellationToken ct)
    {
        var record = _byQuestionId.Values
            .Where(r =>
                r.Status == PendingQuestionStatus.AwaitingComment &&
                r.TelegramChatId == telegramChatId &&
                r.RespondentUserId == respondentUserId)
            .OrderBy(r => r.StoredAt)
            .FirstOrDefault();
        return Task.FromResult<PendingQuestion?>(record);
    }

    public Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<PendingQuestion> expired = _byQuestionId.Values
            .Where(r =>
                (r.Status == PendingQuestionStatus.Pending ||
                 r.Status == PendingQuestionStatus.AwaitingComment) &&
                r.ExpiresAt <= now)
            .ToList();
        return Task.FromResult(expired);
    }

    private void Mutate(string questionId, Func<PendingQuestion, PendingQuestion> mutator)
    {
        _byQuestionId.AddOrUpdate(
            questionId,
            _ => throw new KeyNotFoundException($"PendingQuestion '{questionId}' not found."),
            (_, current) => mutator(current));
    }
}
