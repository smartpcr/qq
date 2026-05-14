using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Storage;

/// <summary>
/// In-memory <see cref="IAgentQuestionStore"/> sufficient for Stage 2.x integration tests
/// before <c>SqlAgentQuestionStore</c> lands in Stage 3.3. Implements the full contract
/// including compare-and-set status transitions, expiry batch scans, and conversation-scoped
/// lookups.
/// </summary>
public sealed class InMemoryAgentQuestionStore : IAgentQuestionStore
{
    private readonly ConcurrentDictionary<string, AgentQuestion> _byId
        = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(AgentQuestion question, CancellationToken ct)
    {
        if (question is null) throw new ArgumentNullException(nameof(question));
        ct.ThrowIfCancellationRequested();

        var toStore = question.CreatedAt == default
            ? question with { CreatedAt = DateTimeOffset.UtcNow }
            : question;

        _byId[toStore.QuestionId] = toStore;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byId.TryGetValue(questionId, out var found);
        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateStatusAsync(
        string questionId,
        string expectedStatus,
        string newStatus,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        while (_byId.TryGetValue(questionId, out var current))
        {
            if (!string.Equals(current.Status, expectedStatus, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            var updated = current with { Status = newStatus };
            if (_byId.TryUpdate(questionId, updated, current))
            {
                return Task.FromResult(true);
            }
            // Lost the race; retry.
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        while (_byId.TryGetValue(questionId, out var current))
        {
            var updated = current with { ConversationId = conversationId };
            if (_byId.TryUpdate(questionId, updated, current))
            {
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var found = _byId.Values
            .Where(q => string.Equals(q.ConversationId, conversationId, StringComparison.Ordinal)
                        && string.Equals(q.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal))
            .OrderByDescending(q => q.CreatedAt)
            .FirstOrDefault();
        return Task.FromResult<AgentQuestion?>(found);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<AgentQuestion> result = _byId.Values
            .Where(q => string.Equals(q.ConversationId, conversationId, StringComparison.Ordinal)
                        && string.Equals(q.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal))
            .OrderByDescending(q => q.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        IReadOnlyList<AgentQuestion> result = _byId.Values
            .Where(q => string.Equals(q.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal)
                        && q.ExpiresAt < cutoff)
            .OrderBy(q => q.ExpiresAt)
            .Take(batchSize)
            .ToList();
        return Task.FromResult(result);
    }
}
