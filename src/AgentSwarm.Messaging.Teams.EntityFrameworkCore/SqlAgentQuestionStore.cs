using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IAgentQuestionStore"/>. Persists the full
/// <see cref="AgentQuestion"/> payload (including the serialized
/// <see cref="AgentQuestion.AllowedActions"/> list) to the
/// <see cref="TeamsLifecycleDbContext.AgentQuestions"/> table per
/// <c>implementation-plan.md</c> §3.3 step 2 and the architecture contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Store-owned creation semantics</b>: <see cref="SaveAsync"/> stamps
/// <see cref="AgentQuestion.Status"/> = <see cref="AgentQuestionStatuses.Open"/> and
/// <see cref="AgentQuestion.CreatedAt"/> = <see cref="DateTimeOffset.UtcNow"/>
/// regardless of the caller-supplied values, satisfying the iter-2 critique that
/// SaveAsync must normalise caller input to the canonical "open / now" initial state.
/// </para>
/// <para>
/// <b>Atomic status transitions</b>: <see cref="TryUpdateStatusAsync"/> uses
/// <c>ExecuteUpdateAsync</c> with a server-side compare-and-set predicate to enforce
/// first-writer-wins semantics. The method also stamps <c>ResolvedAt</c> when the
/// transition lands at a terminal status (<see cref="AgentQuestionStatuses.Resolved"/> or
/// <see cref="AgentQuestionStatuses.Expired"/>).
/// </para>
/// <para>
/// <b>AllowedActions round-trip</b>: the list is serialised to JSON via
/// <see cref="JsonSerializer"/> on write and parsed back on every read so callers
/// receive the typed list and can construct a new
/// <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/> record without any
/// awareness of the EF entity shape.
/// </para>
/// </remarks>
public sealed class SqlAgentQuestionStore : IAgentQuestionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDbContextFactory<TeamsLifecycleDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Construct the store with the DI-bound EF context factory and the system clock.
    /// </summary>
    public SqlAgentQuestionStore(IDbContextFactory<TeamsLifecycleDbContext> contextFactory)
        : this(contextFactory, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-friendly constructor that accepts a deterministic <see cref="TimeProvider"/>
    /// so unit tests can verify the <c>CreatedAt = UtcNow</c> normalization without
    /// flakiness.
    /// </summary>
    public SqlAgentQuestionStore(
        IDbContextFactory<TeamsLifecycleDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task SaveAsync(AgentQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        var errors = question.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "AgentQuestion fails validation: " + string.Join("; ", errors));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entity = new AgentQuestionEntity
        {
            QuestionId = question.QuestionId,
            AgentId = question.AgentId,
            TaskId = question.TaskId,
            TenantId = question.TenantId,
            TargetUserId = question.TargetUserId,
            TargetChannelId = question.TargetChannelId,
            ConversationId = question.ConversationId,
            Title = question.Title,
            Body = question.Body,
            Severity = question.Severity,
            AllowedActionsJson = JsonSerializer.Serialize(question.AllowedActions, JsonOptions),
            ExpiresAt = question.ExpiresAt,
            CorrelationId = question.CorrelationId,
            // Store-owned: SaveAsync always stamps a fresh "Open / now" creation pair
            // regardless of the caller-supplied Status / CreatedAt. Addresses the iter-2
            // critique #2 about normalising caller-supplied non-default values.
            Status = AgentQuestionStatuses.Open,
            CreatedAt = _timeProvider.GetUtcNow(),
            ResolvedAt = null,
        };

        ctx.AgentQuestions.Add(entity);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("QuestionId must be a non-empty string.", nameof(questionId));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await ctx.AgentQuestions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.QuestionId == questionId, ct)
            .ConfigureAwait(false);

        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateStatusAsync(
        string questionId,
        string expectedStatus,
        string newStatus,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("QuestionId must be a non-empty string.", nameof(questionId));
        }

        if (!AgentQuestionStatuses.IsValid(expectedStatus))
        {
            throw new ArgumentException(
                $"expectedStatus '{expectedStatus}' is not one of [{string.Join(", ", AgentQuestionStatuses.All)}].",
                nameof(expectedStatus));
        }

        if (!AgentQuestionStatuses.IsValid(newStatus))
        {
            throw new ArgumentException(
                $"newStatus '{newStatus}' is not one of [{string.Join(", ", AgentQuestionStatuses.All)}].",
                nameof(newStatus));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var isTerminal = string.Equals(newStatus, AgentQuestionStatuses.Resolved, StringComparison.Ordinal)
            || string.Equals(newStatus, AgentQuestionStatuses.Expired, StringComparison.Ordinal);

        var rows = await ctx.AgentQuestions
            .Where(e => e.QuestionId == questionId && e.Status == expectedStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Status, newStatus)
                    .SetProperty(e => e.ResolvedAt, e => isTerminal ? now : e.ResolvedAt),
                ct)
            .ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async Task UpdateConversationIdAsync(
        string questionId,
        string conversationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("QuestionId must be a non-empty string.", nameof(questionId));
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId must be a non-empty string.", nameof(conversationId));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await ctx.AgentQuestions
            .Where(e => e.QuestionId == questionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(e => e.ConversationId, conversationId),
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(
        string conversationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId must be a non-empty string.", nameof(conversationId));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await ctx.AgentQuestions
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.Status == AgentQuestionStatuses.Open)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(
        string conversationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId must be a non-empty string.", nameof(conversationId));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await ctx.AgentQuestions
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.Status == AgentQuestionStatuses.Open)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(Map).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await ctx.AgentQuestions
            .AsNoTracking()
            .Where(e => e.Status == AgentQuestionStatuses.Open && e.ExpiresAt < cutoff)
            .OrderBy(e => e.ExpiresAt)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(Map).ToArray();
    }

    private static AgentQuestion Map(AgentQuestionEntity e)
    {
        var actions = ParseAllowedActions(e.AllowedActionsJson);
        return new AgentQuestion
        {
            QuestionId = e.QuestionId,
            AgentId = e.AgentId,
            TaskId = e.TaskId ?? string.Empty,
            TenantId = e.TenantId,
            TargetUserId = e.TargetUserId,
            TargetChannelId = e.TargetChannelId,
            ConversationId = e.ConversationId,
            Title = e.Title,
            Body = e.Body,
            Severity = e.Severity,
            AllowedActions = actions,
            ExpiresAt = e.ExpiresAt,
            CorrelationId = e.CorrelationId,
            CreatedAt = e.CreatedAt,
            Status = e.Status,
        };
    }

    private static IReadOnlyList<HumanAction> ParseAllowedActions(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<HumanAction>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<HumanAction>>(json, JsonOptions);
            return parsed?.ToArray() ?? (IReadOnlyList<HumanAction>)Array.Empty<HumanAction>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Failed to deserialize AgentQuestion.AllowedActions JSON; the stored row is corrupt.",
                ex);
        }
    }
}
