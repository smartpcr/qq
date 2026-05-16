using System.Data;
using AgentSwarm.Messaging.Teams;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="ICardStateStore"/>. Persists the Teams-specific
/// message identity (<c>ActivityId</c>, <c>ConversationId</c>,
/// <c>ConversationReferenceJson</c>) for each Adaptive Card sent in response to an
/// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/> per
/// <c>implementation-plan.md</c> §3.3 step 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract surface</b>: this store implements only the three methods on
/// <see cref="ICardStateStore"/> (<see cref="SaveAsync"/>, <see cref="GetByQuestionIdAsync"/>,
/// <see cref="UpdateStatusAsync"/>). Any orphan-card cleanup is the responsibility of
/// <see cref="AgentSwarm.Messaging.Teams.ITeamsCardManager"/> on
/// <see cref="AgentSwarm.Messaging.Teams.TeamsMessengerConnector"/> — the store
/// surface is intentionally narrow to preserve the architecture contract.
/// </para>
/// <para>
/// <b>Save semantics</b>: <see cref="SaveAsync"/> performs an atomic upsert — any row
/// already present for the question is deleted and a fresh one is inserted, all inside a
/// <see cref="IsolationLevel.Serializable"/> transaction. The overwrite is intentional
/// (a proactive resend must replace the stale <c>ActivityId</c>/<c>ConversationReferenceJson</c>
/// captured by the previous send), and the SERIALIZABLE isolation level is what makes the
/// delete-then-insert pair safe against concurrent <see cref="SaveAsync"/> callers for the
/// same <c>QuestionId</c>: the database serialises the writers so the second caller cannot
/// race past the first and produce a duplicate primary-key violation.
/// </para>
/// </remarks>
public sealed class SqlCardStateStore : ICardStateStore
{
    private readonly IDbContextFactory<TeamsLifecycleDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Construct the store with the DI-bound EF context factory and the system clock.</summary>
    public SqlCardStateStore(IDbContextFactory<TeamsLifecycleDbContext> contextFactory)
        : this(contextFactory, TimeProvider.System)
    {
    }

    /// <summary>Test-friendly constructor that accepts a deterministic <see cref="TimeProvider"/>.</summary>
    public SqlCardStateStore(
        IDbContextFactory<TeamsLifecycleDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task SaveAsync(TeamsCardState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.QuestionId))
        {
            throw new ArgumentException("QuestionId is required on TeamsCardState.", nameof(state));
        }

        if (string.IsNullOrWhiteSpace(state.ActivityId))
        {
            throw new ArgumentException("ActivityId is required on TeamsCardState.", nameof(state));
        }

        if (string.IsNullOrWhiteSpace(state.ConversationId))
        {
            throw new ArgumentException("ConversationId is required on TeamsCardState.", nameof(state));
        }

        if (string.IsNullOrWhiteSpace(state.ConversationReferenceJson))
        {
            throw new ArgumentException(
                "ConversationReferenceJson is required on TeamsCardState.",
                nameof(state));
        }

        var now = _timeProvider.GetUtcNow();
        var entity = new CardStateEntity
        {
            QuestionId = state.QuestionId,
            ActivityId = state.ActivityId,
            ConversationId = state.ConversationId,
            ConversationReferenceJson = state.ConversationReferenceJson,
            Status = string.IsNullOrWhiteSpace(state.Status) ? TeamsCardStatuses.Pending : state.Status,
            // Preserve caller-supplied CreatedAt/UpdatedAt when meaningful, otherwise stamp now.
            CreatedAt = state.CreatedAt == default ? now : state.CreatedAt,
            UpdatedAt = state.UpdatedAt == default ? now : state.UpdatedAt,
        };

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Atomic upsert. The previous implementation read the row, called Remove on the
        // tracked entity, then Add on a fresh one, and committed the pair with a single
        // SaveChangesAsync. That sequence was a race: two callers writing the same
        // QuestionId could both observe the row, both schedule a Remove, both schedule an
        // Add, and one of the SaveChangesAsync calls would surface a DbUpdateException on
        // the duplicate PK insert.
        //
        // The fix is to perform the delete directly at the database (ExecuteDeleteAsync)
        // and bracket it with the insert inside a SERIALIZABLE transaction. Under
        // SERIALIZABLE isolation the DELETE acquires a key/range lock for the QuestionId;
        // a concurrent SaveAsync for the same QuestionId blocks until this transaction
        // commits, then its DELETE sees and removes the freshly-written row before its
        // own INSERT proceeds. The net result is a "last writer wins" upsert with no
        // duplicate-PK race and no application-level retry loop.
        await using var tx = await ctx.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        await ctx.CardStates
            .Where(e => e.QuestionId == state.QuestionId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        ctx.CardStates.Add(entity);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("QuestionId must be a non-empty string.", nameof(questionId));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await ctx.CardStates
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("QuestionId must be a non-empty string.", nameof(questionId));
        }

        if (!TeamsCardStatuses.IsValid(newStatus))
        {
            throw new ArgumentException(
                $"newStatus '{newStatus}' is not one of [{string.Join(", ", TeamsCardStatuses.All)}].",
                nameof(newStatus));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        await ctx.CardStates
            .Where(e => e.QuestionId == questionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Status, newStatus)
                    .SetProperty(e => e.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);
    }

    private static TeamsCardState Map(CardStateEntity e)
        => new()
        {
            QuestionId = e.QuestionId,
            ActivityId = e.ActivityId,
            ConversationId = e.ConversationId,
            ConversationReferenceJson = e.ConversationReferenceJson,
            Status = e.Status,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };
}
