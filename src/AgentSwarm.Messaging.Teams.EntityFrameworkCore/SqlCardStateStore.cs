using System.Data;
using AgentSwarm.Messaging.Teams;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="ICardStateStore"/>. Persists the Teams-specific
/// message identity (<c>ActivityId</c>, <c>ConversationId</c>,
/// <c>ConversationReferenceJson</c>) for each Adaptive Card sent in response to an
/// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/> per
/// <c>implementation-plan.md</c> ┬º3.3 step 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract surface</b>: this store implements only the three methods on
/// <see cref="ICardStateStore"/> (<see cref="SaveAsync"/>, <see cref="GetByQuestionIdAsync"/>,
/// <see cref="UpdateStatusAsync"/>). Any orphan-card cleanup is the responsibility of
/// <see cref="AgentSwarm.Messaging.Teams.ITeamsCardManager"/> on
/// <see cref="AgentSwarm.Messaging.Teams.TeamsMessengerConnector"/> ΓÇö the store
/// surface is intentionally narrow to preserve the architecture contract.
/// </para>
/// <para>
/// <b>Save semantics</b>: <see cref="SaveAsync"/> performs an atomic upsert by issuing a
/// raw <c>DELETE</c> followed by an <c>INSERT</c> inside a single <see
/// cref="IsolationLevel.Serializable"/> transaction. The delete + insert pair is therefore
/// serialised against concurrent <see cref="SaveAsync"/> callers writing the same
/// <c>QuestionId</c>, which eliminates the read-modify-write race that the previous
/// tracked-entity approach exhibited (two threads both observing the row absent, both
/// attempting to insert, the loser raising a <see cref="DbUpdateException"/> on the
/// duplicate primary key). A proactive resend still overwrites the stale
/// <c>ActivityId</c>/<c>ConversationReferenceJson</c> captured by the previous send so
/// the original upsert semantic is preserved. The work is dispatched through the EF
/// Core execution strategy so a retrying provider configuration (for example SQL Server
/// <c>EnableRetryOnFailure</c>) re-runs the entire transaction on transient failures
/// instead of half-committing.
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

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Wrap the delete + insert in a SERIALIZABLE transaction so concurrent SaveAsync
        // calls for the same QuestionId are serialised at the database level. Without the
        // transaction two callers can both observe the row absent and both attempt to
        // insert, with the loser raising a DbUpdateException on the duplicate primary
        // key. The work is dispatched through the provider execution strategy so a
        // retrying configuration (SQL Server EnableRetryOnFailure) re-runs the entire
        // transaction on transient failures rather than half-committing. SQLite (used by
        // the in-memory test fixture) honours IsolationLevel.Serializable via its native
        // single-writer lock, so the same code path is exercised in tests.
        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            async innerCt =>
            {
                await using var tx = await ctx.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, innerCt)
                    .ConfigureAwait(false);

                await ctx.CardStates
                    .Where(e => e.QuestionId == state.QuestionId)
                    .ExecuteDeleteAsync(innerCt)
                    .ConfigureAwait(false);

                // Detach any tracked entry that survived a prior strategy attempt so the
                // Add below stamps a clean tracker entry. ExecuteDeleteAsync issues a raw
                // DELETE and does not touch the change tracker, so under a happy first
                // attempt this loop is a no-op; it only matters when the execution
                // strategy retries this lambda after a transient failure on a previous
                // pass left a tracked Added entity behind.
                foreach (var tracked in ctx.ChangeTracker
                    .Entries<CardStateEntity>()
                    .Where(e => e.Entity.QuestionId == state.QuestionId)
                    .ToList())
                {
                    tracked.State = EntityState.Detached;
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

                ctx.CardStates.Add(entity);
                await ctx.SaveChangesAsync(innerCt).ConfigureAwait(false);
                await tx.CommitAsync(innerCt).ConfigureAwait(false);
            },
            ct)
            .ConfigureAwait(false);
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
