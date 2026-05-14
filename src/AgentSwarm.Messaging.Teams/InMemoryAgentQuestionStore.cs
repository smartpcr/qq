using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// In-memory <see cref="IAgentQuestionStore"/> implementation backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Registered in Stage 2.1 as the default
/// DI implementation for local development and integration tests; replaced by the
/// SQL-backed <c>SqlAgentQuestionStore</c> in Stage 3.3.
/// </summary>
/// <remarks>
/// <para>
/// Status transitions use compare-and-set semantics — <see cref="TryUpdateStatusAsync"/>
/// atomically swaps the in-flight record under a per-question lock so two concurrent
/// callers cannot both observe success. This mirrors the first-writer-wins protocol
/// mandated by <c>architecture.md</c> §6.3.
/// </para>
/// </remarks>
public sealed class InMemoryAgentQuestionStore : IAgentQuestionStore
{
    private readonly ConcurrentDictionary<string, AgentQuestion> _byId = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(AgentQuestion question, CancellationToken ct)
    {
        if (question is null) throw new ArgumentNullException(nameof(question));
        if (string.IsNullOrWhiteSpace(question.QuestionId))
        {
            throw new ArgumentException("QuestionId must be non-empty.", nameof(question));
        }

        ct.ThrowIfCancellationRequested();

        // Stamp CreatedAt on first save so callers can rely on a non-default UTC instant
        // when reading back.
        var stamped = question.CreatedAt == default
            ? question with { CreatedAt = DateTimeOffset.UtcNow }
            : question;
        _byId[question.QuestionId] = stamped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId)) throw new ArgumentException("QuestionId must be non-empty.", nameof(questionId));
        ct.ThrowIfCancellationRequested();

        _byId.TryGetValue(questionId, out var value);
        return Task.FromResult(value);
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId)) throw new ArgumentException("QuestionId must be non-empty.", nameof(questionId));
        if (string.IsNullOrWhiteSpace(expectedStatus)) throw new ArgumentException("Expected status must be non-empty.", nameof(expectedStatus));
        if (string.IsNullOrWhiteSpace(newStatus)) throw new ArgumentException("New status must be non-empty.", nameof(newStatus));
        ct.ThrowIfCancellationRequested();

        // Loop on TryUpdate so a concurrent SaveAsync (replacing the row) doesn't cause us
        // to give up prematurely — we re-read until we either commit the CAS or observe the
        // status no longer matches.
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
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId)) throw new ArgumentException("QuestionId must be non-empty.", nameof(questionId));
        if (string.IsNullOrWhiteSpace(conversationId)) throw new ArgumentException("ConversationId must be non-empty.", nameof(conversationId));
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
        if (string.IsNullOrWhiteSpace(conversationId)) throw new ArgumentException("ConversationId must be non-empty.", nameof(conversationId));
        ct.ThrowIfCancellationRequested();

        var match = _byId.Values
            .Where(q =>
                string.Equals(q.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal) &&
                string.Equals(q.ConversationId, conversationId, StringComparison.Ordinal))
            .OrderByDescending(q => q.CreatedAt)
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) throw new ArgumentException("ConversationId must be non-empty.", nameof(conversationId));
        ct.ThrowIfCancellationRequested();

        var matches = _byId.Values
            .Where(q =>
                string.Equals(q.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal) &&
                string.Equals(q.ConversationId, conversationId, StringComparison.Ordinal))
            .OrderByDescending(q => q.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<AgentQuestion>>(matches);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");
        ct.ThrowIfCancellationRequested();

        var matches = _byId.Values
            .Where(q =>
                string.Equals(q.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal) &&
                q.ExpiresAt < cutoff)
            .OrderBy(q => q.ExpiresAt)
            .Take(batchSize)
            .ToArray();
        return Task.FromResult<IReadOnlyList<AgentQuestion>>(matches);
    }
}
