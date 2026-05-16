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
    /// <see cref="CommitAsync(IDbContextTransaction, CancellationToken)"/>
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

        for (var i = 0; i < registrations.Count; i++)
        {
            await StageUpsertAsync(db, registrations[i], ct).ConfigureAwait(false);
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
    /// Callers that need atomic batch semantics
    /// (<see cref="RegisterManyAsync(IReadOnlyList{OperatorRegistration}, CancellationToken)"/>)
    /// stage every row into the change tracker before flushing once
    /// inside an
    /// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/>
    /// so a constraint violation on the last entry rolls back all
    /// earlier entries.
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
        }
        else
        {
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
        }
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
