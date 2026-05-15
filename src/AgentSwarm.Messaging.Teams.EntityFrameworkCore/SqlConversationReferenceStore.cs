using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// Entity Framework Core implementation of <see cref="IConversationReferenceStore"/> and
/// <see cref="IConversationReferenceRouter"/> per Stage 4.1 of <c>implementation-plan.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The store backs the <c>ConversationReferences</c> table modeled by
/// <see cref="TeamsConversationReferenceDbContext"/>. Every operation creates a fresh
/// <see cref="DbContext"/> instance via the injected <see cref="IDbContextFactory{TContext}"/>
/// — a singleton <see cref="DbContext"/> would be unsafe for the concurrent reads required
/// by §9 of <c>architecture.md</c> (1000+ concurrent users), and the factory pattern
/// matches the registration helper in
/// <see cref="EntityFrameworkCoreServiceCollectionExtensions.AddSqlConversationReferenceStore"/>.
/// </para>
/// <para>
/// The store also implements <see cref="IConversationReferenceRouter"/> so a single DI
/// registration covers both contracts — see
/// <c>TeamsServiceCollectionExtensions.AddTeamsMessengerConnector</c> which auto-wires
/// the router from a store that implements both interfaces.
/// </para>
/// <para>
/// <b>Dual-key model:</b> records are upserted on either <c>(AadObjectId, TenantId)</c>
/// (user scope) or <c>(ChannelId, TenantId)</c> (channel scope) depending on which natural
/// key the inbound <see cref="TeamsConversationReference"/> carries. The two filtered unique
/// indexes declared by <see cref="TeamsConversationReferenceDbContext"/> guard against
/// duplicate rows per scope while permitting both scopes to coexist in the same table.
/// </para>
/// </remarks>
public sealed class SqlConversationReferenceStore : IConversationReferenceStore, IConversationReferenceRouter
{
    private readonly IDbContextFactory<TeamsConversationReferenceDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Construct the store from an injected EF Core context factory.</summary>
    /// <param name="contextFactory">Factory creating per-call <see cref="TeamsConversationReferenceDbContext"/> instances.</param>
    /// <param name="timeProvider">Source for <c>UpdatedAt</c> / <c>DeactivatedAt</c> timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    public SqlConversationReferenceStore(
        IDbContextFactory<TeamsConversationReferenceDbContext> contextFactory,
        TimeProvider? timeProvider = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (string.IsNullOrEmpty(reference.TenantId))
        {
            throw new ArgumentException(
                "TeamsConversationReference.TenantId must be a non-empty string — every " +
                "conversation reference is keyed by its Entra ID tenant for multi-tenant " +
                "isolation and security.",
                nameof(reference));
        }

        var hasAad = !string.IsNullOrEmpty(reference.AadObjectId);
        var hasChannel = !string.IsNullOrEmpty(reference.ChannelId);

        if (!hasAad && !hasChannel)
        {
            throw new ArgumentException(
                "TeamsConversationReference must specify either AadObjectId (user-scoped) or " +
                "ChannelId (channel-scoped); both were null/empty.",
                nameof(reference));
        }

        if (hasAad && hasChannel)
        {
            throw new ArgumentException(
                "TeamsConversationReference must specify exactly one of AadObjectId " +
                "(user-scoped) or ChannelId (channel-scoped); both were populated. The two " +
                "scopes are mutually exclusive — user-scoped rows have AadObjectId set and " +
                "ChannelId null, channel-scoped rows have ChannelId set and AadObjectId null.",
                nameof(reference));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        ConversationReferenceEntity? existing;

        if (hasAad)
        {
            existing = await context.ConversationReferences
                .FirstOrDefaultAsync(
                    e => e.AadObjectId == reference.AadObjectId && e.TenantId == reference.TenantId,
                    ct)
                .ConfigureAwait(false);
        }
        else
        {
            existing = await context.ConversationReferences
                .FirstOrDefaultAsync(
                    e => e.ChannelId == reference.ChannelId && e.TenantId == reference.TenantId,
                    ct)
                .ConfigureAwait(false);
        }

        if (existing is null)
        {
            var entity = new ConversationReferenceEntity
            {
                Id = string.IsNullOrEmpty(reference.Id) ? Guid.NewGuid().ToString("D") : reference.Id,
                TenantId = reference.TenantId,
                AadObjectId = reference.AadObjectId,
                InternalUserId = reference.InternalUserId,
                ChannelId = reference.ChannelId,
                TeamId = reference.TeamId,
                ServiceUrl = reference.ServiceUrl,
                ConversationId = reference.ConversationId,
                BotId = reference.BotId,
                ConversationJson = reference.ReferenceJson,
                IsActive = true,
                DeactivatedAt = null,
                DeactivationReason = null,
                CreatedAt = reference.CreatedAt == default ? now : reference.CreatedAt,
                UpdatedAt = now,
            };

            context.ConversationReferences.Add(entity);
        }
        else
        {
            existing.AadObjectId = reference.AadObjectId ?? existing.AadObjectId;
            // Preserve a previously-resolved InternalUserId when the inbound reference does
            // not yet carry one — IIdentityResolver may have populated it on a prior message
            // and the resolver is asynchronous (subsequent messages may arrive before the
            // resolver writes back). Letting a fresh save clobber an existing internal ID
            // with null would silently break proactive routing.
            if (!string.IsNullOrEmpty(reference.InternalUserId))
            {
                existing.InternalUserId = reference.InternalUserId;
            }

            existing.ChannelId = reference.ChannelId ?? existing.ChannelId;
            existing.TeamId = reference.TeamId ?? existing.TeamId;
            existing.ServiceUrl = reference.ServiceUrl;
            existing.ConversationId = reference.ConversationId;
            existing.BotId = reference.BotId;
            existing.ConversationJson = reference.ReferenceJson;
            existing.IsActive = true;
            existing.DeactivatedAt = null;
            existing.DeactivationReason = null;
            existing.UpdatedAt = now;
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ValidateKey(tenantId, aadObjectId, nameof(aadObjectId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.AadObjectId == aadObjectId, ct)
            .ConfigureAwait(false);

        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ValidateKey(tenantId, aadObjectId, nameof(aadObjectId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.AadObjectId == aadObjectId && e.IsActive,
                ct)
            .ConfigureAwait(false);

        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
    {
        ValidateKey(tenantId, internalUserId, nameof(internalUserId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.InternalUserId == internalUserId && e.IsActive,
                ct)
            .ConfigureAwait(false);

        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
    {
        ValidateKey(tenantId, channelId, nameof(channelId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.ChannelId == channelId && e.IsActive,
                ct)
            .ConfigureAwait(false);

        return entity is null ? null : Map(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct)
    {
        ValidateKey(tenantId, teamId, nameof(teamId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await context.ConversationReferences
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.TeamId == teamId && e.ChannelId != null && e.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.ConvertAll(Map);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentException("Tenant ID must be a non-empty string.", nameof(tenantId));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await context.ConversationReferences
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.ConvertAll(Map);
    }

    /// <inheritdoc />
    public async Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ValidateKey(tenantId, aadObjectId, nameof(aadObjectId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await context.ConversationReferences
            .AsNoTracking()
            .AnyAsync(
                e => e.TenantId == tenantId && e.AadObjectId == aadObjectId && e.IsActive,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
    {
        ValidateKey(tenantId, internalUserId, nameof(internalUserId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await context.ConversationReferences
            .AsNoTracking()
            .AnyAsync(
                e => e.TenantId == tenantId && e.InternalUserId == internalUserId && e.IsActive,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        ValidateKey(tenantId, channelId, nameof(channelId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await context.ConversationReferences
            .AsNoTracking()
            .AnyAsync(
                e => e.TenantId == tenantId && e.ChannelId == channelId && e.IsActive,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ValidateKey(tenantId, aadObjectId, nameof(aadObjectId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.AadObjectId == aadObjectId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return;
        }

        entity.IsActive = false;
        entity.DeactivatedAt = _timeProvider.GetUtcNow();
        entity.DeactivationReason = ConversationReferenceDeactivationReasons.Uninstalled;
        entity.UpdatedAt = entity.DeactivatedAt.Value;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        ValidateKey(tenantId, channelId, nameof(channelId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.ChannelId == channelId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return;
        }

        entity.IsActive = false;
        entity.DeactivatedAt = _timeProvider.GetUtcNow();
        entity.DeactivationReason = ConversationReferenceDeactivationReasons.Uninstalled;
        entity.UpdatedAt = entity.DeactivatedAt.Value;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ValidateKey(tenantId, aadObjectId, nameof(aadObjectId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.AadObjectId == aadObjectId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return;
        }

        context.ConversationReferences.Remove(entity);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        ValidateKey(tenantId, channelId, nameof(channelId));

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.ChannelId == channelId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return;
        }

        context.ConversationReferences.Remove(entity);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TeamsConversationReference?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            throw new ArgumentException("Conversation ID must be a non-empty string.", nameof(conversationId));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await context.ConversationReferences
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == conversationId && e.IsActive, ct)
            .ConfigureAwait(false);

        return entity is null ? null : Map(entity);
    }

    private static TeamsConversationReference Map(ConversationReferenceEntity e) => new()
    {
        Id = e.Id,
        TenantId = e.TenantId,
        AadObjectId = e.AadObjectId,
        InternalUserId = e.InternalUserId,
        ChannelId = e.ChannelId,
        TeamId = e.TeamId,
        ServiceUrl = e.ServiceUrl,
        ConversationId = e.ConversationId,
        BotId = e.BotId,
        ReferenceJson = e.ConversationJson,
        IsActive = e.IsActive,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };

    private static void ValidateKey(string tenantId, string keyValue, string keyName)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentException("Tenant ID must be a non-empty string.", nameof(tenantId));
        }

        if (string.IsNullOrEmpty(keyValue))
        {
            throw new ArgumentException($"{keyName} must be a non-empty string.", keyName);
        }
    }
}
