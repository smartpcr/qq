using System.Collections.Concurrent;

namespace AgentSwarm.Messaging.Teams.Storage;

/// <summary>
/// In-memory <see cref="ICardStateStore"/> placeholder used until <c>SqlCardStateStore</c>
/// lands in Stage 3.3.
/// </summary>
public sealed class NoOpCardStateStore : ICardStateStore
{
    private readonly ConcurrentDictionary<string, TeamsCardState> _byQuestionId
        = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(TeamsCardState state, CancellationToken ct)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        ct.ThrowIfCancellationRequested();
        _byQuestionId[state.QuestionId] = state;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byQuestionId.TryGetValue(questionId, out var found);
        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        while (_byQuestionId.TryGetValue(questionId, out var current))
        {
            var updated = current with { Status = newStatus, UpdatedAt = DateTimeOffset.UtcNow };
            if (_byQuestionId.TryUpdate(questionId, updated, current))
            {
                return Task.CompletedTask;
            }
        }
        return Task.CompletedTask;
    }
}
