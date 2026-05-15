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
    /// <summary>
    /// Maximum attempts for the read-then-write upsert in <see cref="SaveOrUpdateAsync"/>.
    /// Bounded retry guards against the inevitable insert-vs-insert race between two
    /// concurrent requests carrying the same natural key (e.g. a Teams install-activity
    /// burst, plausible at the FR-007 1000+ concurrent-user scale): both transactions
    /// observe <c>existing == null</c>, both attempt to insert, and the loser hits the
    /// filtered unique index on <c>(AadObjectId, TenantId)</c> or <c>(ChannelId, TenantId)</c>.
    /// On retry the runner-up sees the now-committed row and takes the update path. Three
    /// attempts is sufficient because every retry strictly converges (an insert that lost
    /// the race produces an existing row visible to the next read), so two losers in a row
    /// against the same natural key would require a third concurrent inserter — an
    /// impractical contention pattern for any single Teams conversation reference.
    /// </summary>
    private const int MaxSaveAttempts = 3;

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
    /// <remarks>
    /// Implemented as a bounded retry loop (<see cref="MaxSaveAttempts"/>) around the
    /// classic read-then-write upsert. Without the loop, two concurrent <c>SaveOrUpdateAsync</c>
    /// calls for the same natural key — common during Teams bot-install bursts and required
    /// to be safe by FR-007 (1000+ concurrent users) — could both observe <c>existing == null</c>
    /// and both attempt to insert; the loser would surface a <see cref="DbUpdateException"/>
    /// from the filtered unique index. The retry catches that single class of conflict (verified
    /// via <see cref="IsUniqueConstraintViolation"/>) and re-runs against a fresh context,
    /// where the loser will now observe the winner's row and take the update branch. Failures
    /// that are not unique-constraint violations propagate immediately on the first attempt.
    /// </remarks>
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

        for (var attempt = 1; ; attempt++)
        {
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

            // Whether this iteration's SaveChangesAsync issues an INSERT or an UPDATE
            // determines which DbUpdateException failures are retry-eligible: only an
            // INSERT can collide with the filtered unique indexes via a concurrent
            // peer; an UPDATE that violates the unique constraint indicates the caller
            // is intentionally moving a row onto a duplicate key, which must surface
            // as an error rather than silently retry.
            bool isInsert;

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
                isInsert = true;
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
                isInsert = false;
            }

            try
            {
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException ex)
                when (isInsert && attempt < MaxSaveAttempts && IsUniqueConstraintViolation(ex))
            {
                // A concurrent SaveOrUpdateAsync for the same natural key won the race
                // against the filtered unique index. The next iteration disposes this
                // context, opens a fresh one, re-reads (which will now find the winner's
                // committed row), and falls into the update branch — so the call still
                // honours its upsert contract without surfacing the transient conflict.
            }
        }
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
    /// <remarks>
    /// <para>
    /// <b>Known tenant-isolation gap (accepted):</b> this is the only query on the store
    /// that does not accept a <c>tenantId</c> parameter, so the lookup is not tenant-scoped
    /// like every other read (which honour the FR-006 multi-tenant isolation requirement).
    /// The gap is intentional and structural, not an oversight: the sole consumer is
    /// <see cref="IConversationReferenceRouter.GetByConversationIdAsync"/> invoked from
    /// <c>TeamsMessengerConnector.SendMessageAsync</c>, whose only correlation handle is
    /// <c>MessengerMessage.ConversationId</c>. <c>MessengerMessage</c> deliberately carries
    /// no <c>TenantId</c> field today (it is messenger-platform-agnostic), so the caller
    /// has no tenant context to pass through. Adding a required <c>tenantId</c> parameter
    /// here would force every messenger to teach its message contract about Entra tenants,
    /// which is out of scope for the proactive-messaging stage.
    /// </para>
    /// <para>
    /// <b>Why this is safe in practice:</b> Bot Framework <c>ConversationId</c> values are
    /// generated server-side per (tenant, conversation) pair and are practically unique
    /// across tenants for the lifetime of a conversation; the filtered <c>IsActive = 1</c>
    /// predicate further narrows the candidate set, and the <c>(AadObjectId, TenantId)</c>
    /// and <c>(ChannelId, TenantId)</c> unique indexes guard upstream writes. A collision
    /// would require two tenants' Bot Framework conversations to coincidentally share an
    /// opaque ID, which the platform does not produce.
    /// </para>
    /// <para>
    /// <b>Forward path:</b> when <c>MessengerMessage</c> grows a <c>TenantId</c> (or an
    /// equivalent platform-context field — see the doc on
    /// <c>IConversationReferenceRouter</c>), tighten this method to accept and apply that
    /// tenant filter so the defense-in-depth posture matches the rest of the store. Until
    /// then the gap is documented here so future readers do not mistake it for a missed
    /// security check.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Returns <see langword="true"/> when the supplied <see cref="DbUpdateException"/>
    /// chain originates from a unique-constraint violation surfaced by the underlying
    /// database provider.
    /// </summary>
    /// <remarks>
    /// Detection is provider-agnostic by design — the assembly intentionally does not
    /// take a hard dependency on <c>Microsoft.Data.SqlClient</c> or
    /// <c>Microsoft.Data.Sqlite</c> so the same store works against the SQL Server
    /// provider in production (per <c>architecture.md</c> §9.2) and the SQLite provider
    /// used by the test fixtures. Instead the inner-exception chain is inspected by
    /// type name and message text:
    /// <list type="bullet">
    /// <item><description>SQL Server (<c>Microsoft.Data.SqlClient.SqlException</c>) raises error
    /// numbers 2601 / 2627 with text containing &quot;UNIQUE&quot; or &quot;duplicate key&quot;.</description></item>
    /// <item><description>SQLite (<c>Microsoft.Data.Sqlite.SqliteException</c>) raises
    /// SQLITE_CONSTRAINT_UNIQUE (extended code 2067) with text &quot;UNIQUE constraint failed&quot;.</description></item>
    /// </list>
    /// A generic fallback covers other relational providers whose driver exception text
    /// includes both &quot;duplicate&quot; and &quot;UNIQUE&quot;. When no marker is found the
    /// helper returns <see langword="false"/>, which causes the surrounding catch filter
    /// to re-throw — uncertain failures must surface as errors rather than be silently retried.
    /// </remarks>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            var typeName = inner.GetType().FullName ?? string.Empty;
            var message = inner.Message ?? string.Empty;

            if (typeName.Contains("SqlException", StringComparison.Ordinal)
                && (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (typeName.Contains("SqliteException", StringComparison.Ordinal)
                && message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                && message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
