using System.Collections.Concurrent;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// In-memory <see cref="ICardStateStore"/> stub used as the pre-Stage 3.3 placeholder.
/// Backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by
/// <c>QuestionId</c>. Replaced by the SQL-backed <c>SqlCardStateStore</c> in Stage 3.3.
/// </summary>
public sealed class NoOpCardStateStore : ICardStateStore
{
    private readonly ConcurrentDictionary<string, TeamsCardState> _byQuestionId = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(TeamsCardState state, CancellationToken ct)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (string.IsNullOrWhiteSpace(state.QuestionId))
        {
            throw new ArgumentException("QuestionId must be non-empty.", nameof(state));
        }

        ct.ThrowIfCancellationRequested();
        _byQuestionId[state.QuestionId] = state;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId)) throw new ArgumentException("QuestionId must be non-empty.", nameof(questionId));
        ct.ThrowIfCancellationRequested();

        _byQuestionId.TryGetValue(questionId, out var value);
        return Task.FromResult(value);
    }

    /// <inheritdoc />
    public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId)) throw new ArgumentException("QuestionId must be non-empty.", nameof(questionId));
        if (string.IsNullOrWhiteSpace(newStatus)) throw new ArgumentException("Status must be non-empty.", nameof(newStatus));
        ct.ThrowIfCancellationRequested();

        while (_byQuestionId.TryGetValue(questionId, out var current))
        {
            var updated = current with { Status = newStatus, UpdatedAt = DateTimeOffset.UtcNow };
            if (_byQuestionId.TryUpdate(questionId, updated, current))
            {
                break;
            }
        }

        return Task.CompletedTask;
    }
}
