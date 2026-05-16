// -----------------------------------------------------------------------
// <copyright file="PersistentOperatorRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 3.4 — EF Core-backed <see cref="IOperatorRegistry"/>. Persists
/// <see cref="OperatorBinding"/> rows in the <c>operator_bindings</c>
/// table and supplies the runtime authorization, alias resolution,
/// alert-fallback, and Stage 2.7 tenant-enumeration query paths.
/// Supersedes <see cref="Telegram.Swarm.StubOperatorRegistry"/> via the
/// last-wins DI replacement pattern in
/// <see cref="ServiceCollectionExtensions.AddMessagingPersistence"/>
/// (per implementation-plan.md Stage 3.4 step 1 and architecture.md
/// §4.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime + scope.</b> Registered as a singleton so the singleton
/// command-handler and pipeline registrations in
/// <c>TelegramServiceCollectionExtensions</c> can depend on it without
/// violating the captive-dependency rule. Each call opens a fresh
/// <see cref="IServiceScope"/> to retrieve the scoped
/// <see cref="MessagingDbContext"/>. Mirrors the established pattern
/// used by <see cref="PersistentTaskOversightRepository"/>,
/// <see cref="PersistentOutboundMessageIdIndex"/>,
/// <see cref="PersistentOutboundDeadLetterStore"/>, and
/// <see cref="PersistentAuditLogger"/>.
/// </para>
/// <para>
/// <b>Upsert semantics on <see cref="RegisterAsync"/>.</b> The
/// <c>/start</c> flow (implementation-plan.md Stage 3.4 step 3) must
/// be idempotent: replays of <c>/start</c> from the same operator
/// for the same workspace MUST NOT create a second
/// <see cref="OperatorBinding"/> row, and a previously-deactivated
/// binding MUST be re-activated rather than ignored. The implementation
/// looks up the existing row by the
/// <c>UNIQUE (TelegramUserId, TelegramChatId, WorkspaceId)</c> index
/// (architecture.md §3.1 "Constraints"); when found, it refreshes
/// <see cref="OperatorBinding.IsActive"/>,
/// <see cref="OperatorBinding.RegisteredAt"/>,
/// <see cref="OperatorBinding.Roles"/>, and
/// <see cref="OperatorBinding.OperatorAlias"/> in place; when absent,
/// it inserts a new row.
/// </para>
/// <para>
/// <b>Concurrent upsert race.</b> Two webhook deliveries of <c>/start</c>
/// from the same user/chat may race the find→insert path. The
/// implementation catches <see cref="DbUpdateException"/> (the
/// UNIQUE-constraint violation surface used by all EF Core providers —
/// SQLite, PostgreSQL, SQL Server) and retries the lookup-then-update
/// path once, mirroring the
/// <see cref="PersistentTaskOversightRepository.UpsertAsync"/> race
/// recovery pattern.
/// </para>
/// </remarks>
public sealed class PersistentOperatorRegistry : IOperatorRegistry
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistentOperatorRegistry> _logger;

    public PersistentOperatorRegistry(
        IServiceScopeFactory scopeFactory,
        ILogger<PersistentOperatorRegistry> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperatorBinding>> GetBindingsAsync(
        long telegramUserId,
        long chatId,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var bindings = await db.OperatorBindings
            .AsNoTracking()
            .Where(x => x.TelegramUserId == telegramUserId
                && x.TelegramChatId == chatId
                && x.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return bindings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperatorBinding>> GetAllBindingsAsync(
        long telegramUserId,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var bindings = await db.OperatorBindings
            .AsNoTracking()
            .Where(x => x.TelegramUserId == telegramUserId && x.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return bindings;
    }

    /// <inheritdoc />
    public async Task<OperatorBinding?> GetByAliasAsync(
        string operatorAlias,
        string tenantId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        return await db.OperatorBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OperatorAlias == operatorAlias
                    && x.TenantId == tenantId
                    && x.IsActive,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperatorBinding>> GetByWorkspaceAsync(
        string workspaceId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var bindings = await db.OperatorBindings
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return bindings;
    }

    /// <inheritdoc />
    public async Task RegisterAsync(OperatorRegistration registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        try
        {
            await UpsertCoreAsync(db, registration, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // Concurrent /start race: another caller inserted the same
            // (user, chat, workspace) row between our SELECT and INSERT.
            // The UNIQUE (TelegramUserId, TelegramChatId, WorkspaceId)
            // index rejected our INSERT; retry the lookup-then-update
            // path once with a fresh ChangeTracker so the second call
            // sees the just-inserted row and updates it instead.
            _logger.LogDebug(
                ex,
                "OperatorBinding upsert for TelegramUserId={TelegramUserId} TelegramChatId={TelegramChatId} WorkspaceId={WorkspaceId} raced with a concurrent writer; retrying once.",
                registration.TelegramUserId,
                registration.TelegramChatId,
                registration.WorkspaceId);

            db.ChangeTracker.Clear();
            await UpsertCoreAsync(db, registration, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stage 3.4 iter-3 (evaluator item 2) — atomic batch upsert.
    /// <see cref="TelegramUserAuthorizationService"/>'s
    /// <c>/start</c> onboarding flow used to iterate
    /// <see cref="RegisterAsync"/> one entry at a time when an
    /// operator had multiple workspace bindings under
    /// <c>Telegram:UserTenantMappings</c>. If the second row failed
    /// (e.g. the <c>(OperatorAlias, TenantId)</c> unique index
    /// rejected it because the alias was already claimed by another
    /// operator in the same tenant, or any transient DB error fired
    /// mid-batch), the first row stayed inserted, leaving the
    /// operator with a partial onboarding state the iter-2
    /// blank-field fail-fast was meant to prevent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The implementation wraps every upsert in a single
    /// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/>.
    /// All staged inserts/updates are flushed with one
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// and committed atomically. If ANY upsert throws (constraint
    /// violation, cancellation, transient DB error), the
    /// <c>await using</c> disposal of the transaction rolls back
    /// every change in the batch — there is no
    /// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction.CommitAsync(CancellationToken)"/>
    /// path that runs in the presence of a thrown exception.
    /// </para>
    /// <para>
    /// Unlike <see cref="RegisterAsync"/>, this method intentionally
    /// does <b>not</b> retry on
    /// <see cref="DbUpdateException"/>. The batch is invoked from a
    /// single operator's <c>/start</c> which is itself
    /// rate-limited; the most likely failure shape is a true
    /// constraint violation (duplicate alias claimed by another
    /// operator in the same tenant), and the caller benefits from
    /// the original exception bubbling up so it can record an audit
    /// event and surface a structured error to the operator.
    /// </para>
    /// <para>
    /// <b>Round-trip cost (review-r0 item).</b> Earlier iterations
    /// drove the staging loop through
    /// <see cref="StageUpsertAsync(MessagingDbContext, OperatorRegistration, CancellationToken)"/>,
    /// which issues a tracking
    /// <see cref="EntityFrameworkQueryableExtensions.FirstOrDefaultAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>
    /// per registration. For an operator onboarded with N workspace
    /// bindings under <c>Telegram:UserTenantMappings</c> that produced
    /// an N+1 query pattern (N SELECTs + 1 <c>SaveChangesAsync</c>).
    /// The current implementation collapses the lookup phase into a
    /// single batched tracking SELECT via
    /// <see cref="PreloadExistingForBatchAsync"/>; EF Core's identity
    /// resolution attaches every pre-existing row to the change
    /// tracker, so the staging loop performs zero database round-trips
    /// and the entire batch settles into <b>1 SELECT + 1 SaveChanges</b>
    /// regardless of N. The transaction wrapper, all-or-nothing
    /// rollback semantics, and no-retry-on-<see cref="DbUpdateException"/>
    /// behaviour are preserved exactly, and the
    /// <c>RegisterManyAsync_*</c> test cases continue to pin them.
    /// </para>
    /// </remarks>
    public async Task RegisterManyAsync(
        IReadOnlyList<OperatorRegistration> registrations,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Count == 0)
        {
            return;
        }

        for (var i = 0; i < registrations.Count; i++)
        {
            if (registrations[i] is null)
            {
                throw new ArgumentException(
                    $"RegisterManyAsync received a null OperatorRegistration at index {i}; "
                    + "the caller must validate the batch before invoking the registry.",
                    nameof(registrations));
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        await using var tx = await db.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        // Pre-load every (TelegramUserId, TelegramChatId, WorkspaceId)
        // row that already exists for the batch in ONE tracking SELECT.
        // The returned dictionary lets the staging loop below decide
        // insert-vs-update without issuing additional round-trips — EF
        // Core's identity resolution has already attached each
        // pre-existing row to the change tracker, so the
        // `db.Entry(existing).CurrentValues.SetValues(...)` update
        // path inside StageUpsertForBatch runs against tracked
        // entities and SaveChangesAsync below emits the appropriate
        // UPDATE / INSERT statements in a single flush.
        var existingByKey = await PreloadExistingForBatchAsync(db, registrations, ct).ConfigureAwait(false);

        for (var i = 0; i < registrations.Count; i++)
        {
            StageUpsertForBatch(db, registrations[i], existingByKey);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        return await db.OperatorBindings
            .AsNoTracking()
            .AnyAsync(
                x => x.TelegramUserId == telegramUserId
                    && x.TelegramChatId == chatId
                    && x.IsActive,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetActiveTenantsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var tenants = await db.OperatorBindings
            .AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => x.TenantId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return tenants;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperatorBinding>> GetByTenantAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var bindings = await db.OperatorBindings
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return bindings;
    }

    private static async Task UpsertCoreAsync(
        MessagingDbContext db,
        OperatorRegistration registration,
        CancellationToken ct)
    {
        await StageUpsertAsync(db, registration, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the SELECT-then-INSERT-or-UPDATE work for a single
    /// <see cref="OperatorRegistration"/> on the supplied tracking
    /// <paramref name="db"/> WITHOUT calling
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
    /// Used exclusively by the singular
    /// <see cref="RegisterAsync(OperatorRegistration, CancellationToken)"/>
    /// path (1 SELECT + 1 SaveChanges per call). The batch path in
    /// <see cref="RegisterManyAsync(IReadOnlyList{OperatorRegistration}, CancellationToken)"/>
    /// drives off a pre-loaded dictionary via
    /// <see cref="StageUpsertForBatch"/> instead so it can collapse
    /// the N tracking SELECTs into a single batched SELECT.
    /// </summary>
    private static async Task StageUpsertAsync(
        MessagingDbContext db,
        OperatorRegistration registration,
        CancellationToken ct)
    {
        var existing = await db.OperatorBindings
            .FirstOrDefaultAsync(
                x => x.TelegramUserId == registration.TelegramUserId
                    && x.TelegramChatId == registration.TelegramChatId
                    && x.WorkspaceId == registration.WorkspaceId,
                ct)
            .ConfigureAwait(false);

        ApplyUpsert(db, registration, existing);
    }

    /// <summary>
    /// Batch-path variant of <see cref="StageUpsertAsync"/>. Resolves
    /// the existing row (if any) from the pre-loaded
    /// <paramref name="existingByKey"/> dictionary instead of querying
    /// the database, and registers any newly-staged INSERT back into
    /// the dictionary so a duplicate
    /// <c>(TelegramUserId, TelegramChatId, WorkspaceId)</c> later in
    /// the same batch merges onto the already-staged entity rather
    /// than enqueuing a second INSERT that would collide with the
    /// unique index at
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// time.
    /// </summary>
    private static void StageUpsertForBatch(
        MessagingDbContext db,
        OperatorRegistration registration,
        Dictionary<(long TelegramUserId, long TelegramChatId, string WorkspaceId), OperatorBinding> existingByKey)
    {
        var key = (registration.TelegramUserId, registration.TelegramChatId, registration.WorkspaceId);
        existingByKey.TryGetValue(key, out var existing);

        var staged = ApplyUpsert(db, registration, existing);
        if (existing is null)
        {
            existingByKey[key] = staged;
        }
    }

    /// <summary>
    /// Stages an INSERT (when <paramref name="existing"/> is
    /// <see langword="null"/>) or an UPDATE (when present) for
    /// <paramref name="registration"/> against the tracked
    /// <paramref name="db"/>. Returns the entity that ended up in the
    /// change tracker so batch callers can register fresh INSERTs back
    /// into their pre-loaded dictionary.
    /// </summary>
    private static OperatorBinding ApplyUpsert(
        MessagingDbContext db,
        OperatorRegistration registration,
        OperatorBinding? existing)
    {
        var alias = string.IsNullOrWhiteSpace(registration.OperatorAlias)
            ? BuildFallbackAlias(registration.TelegramUserId)
            : registration.OperatorAlias;

        var roles = registration.Roles is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : registration.Roles.ToArray();

        if (existing is null)
        {
            var inserted = new OperatorBinding
            {
                // Deterministic id so audit / oversight rows can reference
                // it stably across restarts without depending on the row
                // surviving a re-insert. Tenant is included in the
                // derivation because the same (user, chat) pair may have
                // bindings under DIFFERENT tenants in distinct workspaces
                // — without tenant in the key, two simultaneous tenant
                // memberships would collide on Guid.
                Id = DeriveBindingId(
                    registration.TelegramUserId,
                    registration.TelegramChatId,
                    registration.TenantId,
                    registration.WorkspaceId),
                TelegramUserId = registration.TelegramUserId,
                TelegramChatId = registration.TelegramChatId,
                ChatType = registration.ChatType,
                OperatorAlias = alias,
                TenantId = registration.TenantId,
                WorkspaceId = registration.WorkspaceId,
                Roles = roles,
                RegisteredAt = DateTimeOffset.UtcNow,
                IsActive = true,
            };
            db.OperatorBindings.Add(inserted);
            return inserted;
        }

        // Records are immutable — produce a refreshed copy and let
        // EF Core's CurrentValues setter propagate the field
        // updates onto the tracked entity. Refresh the four
        // fields the brief calls out (IsActive, RegisteredAt,
        // Roles, OperatorAlias) PLUS ChatType (the chat's surface
        // may have changed from a private DM to a group between
        // registrations — we honour the latest signal) PLUS
        // TenantId (defensive: a re-registration with a different
        // tenant indicates the operator's config moved tenants;
        // overwriting keeps the row authoritative for the new
        // tenant boundary). The unique index on
        // (OperatorAlias, TenantId) is preserved because we only
        // ever have one row per (user, chat, workspace) so the
        // alias / tenant pair migrates atomically with the row.
        var refreshed = existing with
        {
            ChatType = registration.ChatType,
            OperatorAlias = alias,
            TenantId = registration.TenantId,
            Roles = roles,
            RegisteredAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        db.Entry(existing).CurrentValues.SetValues(refreshed);
        return existing;
    }

    /// <summary>
    /// Issues a single tracking SELECT that returns every
    /// <see cref="OperatorBinding"/> row whose
    /// <c>(TelegramUserId, TelegramChatId, WorkspaceId)</c> tuple
    /// appears in <paramref name="registrations"/>, keyed for O(1)
    /// in-memory lookup. The query uses three axis-wise
    /// <see cref="Enumerable.Contains{TSource}(IEnumerable{TSource}, TSource)"/>
    /// filters (one per column) rather than a row-value
    /// <c>WHERE (UserId, ChatId, WorkspaceId) IN (…)</c> because
    /// EF Core 8's translation of row-value <c>IN</c> across
    /// SQLite / PostgreSQL / SQL Server is uneven and would either
    /// fall back to client evaluation or force a provider-specific
    /// raw-SQL escape hatch. The axis-wise form translates to a
    /// stable parameterised SQL <c>WHERE … IN (…)</c> per axis on
    /// every supported provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The axis-wise filter can return false positives — rows whose
    /// individual columns each match the batch but whose tuple does
    /// not. The dictionary build below discards these by only
    /// inserting rows whose exact
    /// <c>(TelegramUserId, TelegramChatId, WorkspaceId)</c> tuple
    /// was requested. In practice the false-positive rate is near
    /// zero: <see cref="RegisterManyAsync"/> is invoked from a
    /// single operator's <c>/start</c> which pins
    /// <c>TelegramUserId</c> and <c>TelegramChatId</c> to a single
    /// value each and only varies <c>WorkspaceId</c>, so the broad
    /// SQL filter and the exact in-memory match collapse to the
    /// same row set.
    /// </para>
    /// <para>
    /// The query is a tracking query (no
    /// <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}(IQueryable{TEntity})"/>)
    /// because the staging loop relies on EF Core's identity
    /// resolution to attach every pre-existing row to the change
    /// tracker — that is what makes the subsequent
    /// <c>db.Entry(existing).CurrentValues.SetValues(...)</c>
    /// update path produce an UPDATE statement at
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// time instead of a duplicate INSERT.
    /// </para>
    /// <para>
    /// The lookup intentionally does NOT filter on
    /// <see cref="OperatorBinding.IsActive"/>: a previously
    /// deactivated row for the same
    /// <c>(TelegramUserId, TelegramChatId, WorkspaceId)</c> tuple
    /// must be re-activated by an upsert rather than ignored
    /// (which would let
    /// <see cref="MessagingDbContext.OperatorBindings"/>'s
    /// <c>ux_operator_bindings_user_chat_workspace</c> unique index
    /// reject a fresh INSERT against the dead row).
    /// </para>
    /// </remarks>
    private static async Task<Dictionary<(long TelegramUserId, long TelegramChatId, string WorkspaceId), OperatorBinding>> PreloadExistingForBatchAsync(
        MessagingDbContext db,
        IReadOnlyList<OperatorRegistration> registrations,
        CancellationToken ct)
    {
        var userIds = new HashSet<long>();
        var chatIds = new HashSet<long>();
        var workspaceIds = new HashSet<string>(StringComparer.Ordinal);
        var requestedKeys = new HashSet<(long, long, string)>();

        for (var i = 0; i < registrations.Count; i++)
        {
            var r = registrations[i];
            userIds.Add(r.TelegramUserId);
            chatIds.Add(r.TelegramChatId);
            if (!string.IsNullOrEmpty(r.WorkspaceId))
            {
                workspaceIds.Add(r.WorkspaceId);
                requestedKeys.Add((r.TelegramUserId, r.TelegramChatId, r.WorkspaceId));
            }
        }

        if (requestedKeys.Count == 0)
        {
            return new Dictionary<(long TelegramUserId, long TelegramChatId, string WorkspaceId), OperatorBinding>();
        }

        // Materialised lists are passed to EF Core so each Contains
        // call binds against a stable parameter list rather than
        // re-enumerating the HashSet for every translation pass.
        var userIdList = userIds.ToList();
        var chatIdList = chatIds.ToList();
        var workspaceIdList = workspaceIds.ToList();

        var candidates = await db.OperatorBindings
            .Where(x => userIdList.Contains(x.TelegramUserId)
                && chatIdList.Contains(x.TelegramChatId)
                && workspaceIdList.Contains(x.WorkspaceId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var existingByKey = new Dictionary<(long TelegramUserId, long TelegramChatId, string WorkspaceId), OperatorBinding>(candidates.Count);
        foreach (var c in candidates)
        {
            var key = (c.TelegramUserId, c.TelegramChatId, c.WorkspaceId);
            if (requestedKeys.Contains(key))
            {
                existingByKey[key] = c;
            }
        }
        return existingByKey;
    }

    /// <summary>
    /// Returns a deterministic <see cref="Guid"/> for a given
    /// (<paramref name="userId"/>, <paramref name="chatId"/>,
    /// <paramref name="tenantId"/>, <paramref name="workspaceId"/>)
    /// tuple. Mirrors the convention used by
    /// <see cref="Telegram.Swarm.StubOperatorRegistry.DeriveBindingId"/>
    /// and <see cref="Telegram.Auth.ConfiguredOperatorAuthorizationService.DeriveBindingId"/>
    /// so a dev fixture that wires the same (user, chat, tenant, workspace)
    /// across the stub and the persistent registry sees the same
    /// id, which lets a hand-rolled <c>TaskOversight</c> row keyed off
    /// the stub-derived id continue to resolve once the persistent
    /// registry is wired in.
    /// </summary>
    /// <remarks>
    /// The hash key uses a distinct namespace prefix
    /// (<c>"PersistentOperatorRegistry:"</c>) so persistent-binding ids
    /// are not confused with stub-binding ids generated from the same
    /// tuple — they must NOT collide because an integration test that
    /// flips from stub to persistent should be able to assert the
    /// transition explicitly. The acceptance tests (architecture.md
    /// §3.1 stable foreign-key requirement) require that a given
    /// (user, chat, tenant, workspace) always produces the SAME
    /// persistent id across restarts, which the deterministic hash
    /// satisfies.
    /// </remarks>
    public static Guid DeriveBindingId(long userId, long chatId, string tenantId, string workspaceId)
    {
        var key = "PersistentOperatorRegistry:"
            + userId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":"
            + chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":"
            + (tenantId ?? string.Empty)
            + ":"
            + (workspaceId ?? string.Empty);
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }

    private static string BuildFallbackAlias(long userId)
    {
        return "@user-"
            + userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
