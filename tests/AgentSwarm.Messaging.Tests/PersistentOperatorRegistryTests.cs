using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.4 — round-trip tests for
/// <see cref="PersistentOperatorRegistry"/> against an in-memory
/// SQLite connection using the real <see cref="MessagingDbContext"/>
/// schema. Pins all six (eight including the Stage 2.7 additions)
/// methods of <see cref="IOperatorRegistry"/>, the upsert semantics
/// on <c>RegisterAsync</c>, and the tenant-scoped alias resolution
/// (per architecture.md lines 116–119) required by the brief's
/// Test Scenarios:
/// <list type="bullet">
///   <item><description>Authorized user mapped</description></item>
///   <item><description>Binding persists across restart</description></item>
///   <item><description>Alias lookup resolves binding within tenant
///   (and returns null cross-tenant)</description></item>
///   <item><description>Multi-workspace bindings returned</description></item>
/// </list>
/// </summary>
public sealed class PersistentOperatorRegistryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private PersistentOperatorRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        await using (var scope = _provider.CreateAsyncScope())
        await using (var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _registry = new PersistentOperatorRegistry(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PersistentOperatorRegistry>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RegisterAsync_InsertsNewBinding_AndGetBindingsReturnsIt()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice", roles: new[] { "Operator", "Approver" }),
            default);

        var bindings = await _registry.GetBindingsAsync(12345, 67890, default);
        bindings.Should().HaveCount(1);

        var b = bindings[0];
        b.TelegramUserId.Should().Be(12345);
        b.TelegramChatId.Should().Be(67890);
        b.TenantId.Should().Be("t-1");
        b.WorkspaceId.Should().Be("ws-alpha");
        b.OperatorAlias.Should().Be("@alice");
        b.Roles.Should().BeEquivalentTo(new[] { "Operator", "Approver" });
        b.IsActive.Should().BeTrue();
        b.ChatType.Should().Be(ChatType.Private);
    }

    [Fact]
    public async Task RegisterAsync_DuplicateForSameWorkspace_UpsertsInPlace()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice", roles: new[] { "Operator" }),
            default);

        // Replay /start with refreshed roles + alias — must upsert,
        // NOT insert a second row (UNIQUE (UserId, ChatId, WorkspaceId)).
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice-renamed", roles: new[] { "Operator", "Approver" }),
            default);

        var bindings = await _registry.GetBindingsAsync(12345, 67890, default);
        bindings.Should().HaveCount(1, "the same (user, chat, workspace) triple must produce exactly one row");
        bindings[0].OperatorAlias.Should().Be("@alice-renamed");
        bindings[0].Roles.Should().BeEquivalentTo(new[] { "Operator", "Approver" });
        bindings[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsync_RecreatesDeactivatedBinding_AsActive()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice"),
            default);

        // Soft-disable the row directly via the DbContext to simulate
        // an admin deactivation.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var row = await db.OperatorBindings.SingleAsync();
            db.Entry(row).CurrentValues.SetValues(row with { IsActive = false });
            await db.SaveChangesAsync();
        }

        // Replay /start — the brief requires the registry to re-activate
        // the binding rather than reject the registration.
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice"),
            default);

        var bindings = await _registry.GetBindingsAsync(12345, 67890, default);
        bindings.Should().ContainSingle(b => b.IsActive);
    }

    [Fact]
    public async Task GetBindingsAsync_OnlyReturnsActiveBindings()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice"),
            default);

        // Deactivate the binding.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var row = await db.OperatorBindings.SingleAsync();
            db.Entry(row).CurrentValues.SetValues(row with { IsActive = false });
            await db.SaveChangesAsync();
        }

        var bindings = await _registry.GetBindingsAsync(12345, 67890, default);
        bindings.Should().BeEmpty(
            "deactivated bindings must NOT appear in GetBindingsAsync — they would silently re-authorize a disabled operator");
    }

    [Fact]
    public async Task IsAuthorizedAsync_ReturnsTrueWhenActiveBindingExists()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha"),
            default);

        (await _registry.IsAuthorizedAsync(12345, 67890, default)).Should().BeTrue();
        (await _registry.IsAuthorizedAsync(99999, 67890, default)).Should().BeFalse();
        (await _registry.IsAuthorizedAsync(12345, 11111, default)).Should().BeFalse();
    }

    [Fact]
    public async Task BindingPersistsAcrossRegistryRestart()
    {
        // Brief Test Scenario: "Binding persists across restart —
        // Given Telegram user 12345 sends /start and an OperatorBinding
        // row is created, When the service restarts and user 12345
        // sends /status from the same chat, Then IsAuthorizedAsync
        // returns true because the binding is persisted in the database."
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha"),
            default);

        // Simulate restart: build a fresh registry instance over the
        // SAME SqliteConnection (representing the same on-disk db).
        var fresh = new PersistentOperatorRegistry(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PersistentOperatorRegistry>.Instance);

        (await fresh.IsAuthorizedAsync(12345, 67890, default)).Should().BeTrue(
            "the binding row must survive a registry instance restart because it lives in the persistent operator_bindings table");
        var bindings = await fresh.GetBindingsAsync(12345, 67890, default);
        bindings.Should().HaveCount(1);
        bindings[0].WorkspaceId.Should().Be("ws-alpha");
    }

    [Fact]
    public async Task GetByAliasAsync_ResolvesWithinTenant_AndReturnsNullAcrossTenant()
    {
        // Brief Test Scenario: tenant-scoped alias resolution per
        // architecture.md lines 116–119.
        await _registry.RegisterAsync(NewRegistration(
                userId: 100, chatId: 200, tenantId: "acme", workspaceId: "ws-1",
                alias: "@operator-1"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 101, chatId: 201, tenantId: "other-tenant", workspaceId: "ws-1",
                alias: "@operator-2"),
            default);

        var inTenant = await _registry.GetByAliasAsync("@operator-1", "acme", default);
        inTenant.Should().NotBeNull();
        inTenant!.TelegramUserId.Should().Be(100);
        inTenant.TelegramChatId.Should().Be(200);

        var crossTenant = await _registry.GetByAliasAsync("@operator-1", "other-tenant", default);
        crossTenant.Should().BeNull(
            "alias resolution must be tenant-scoped (architecture.md lines 116-119) — @operator-1 in tenant acme MUST NOT resolve in tenant other-tenant");
    }

    [Fact]
    public async Task GetByAliasAsync_AcrossTenantsWithSameAlias_ResolvesSeparately()
    {
        // Two tenants may independently use the same alias without
        // collision (architecture.md lines 116-119).
        await _registry.RegisterAsync(NewRegistration(
                userId: 100, chatId: 200, tenantId: "acme", workspaceId: "ws-1",
                alias: "@alice"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 200, chatId: 300, tenantId: "globex", workspaceId: "ws-1",
                alias: "@alice"),
            default);

        var acmeAlice = await _registry.GetByAliasAsync("@alice", "acme", default);
        acmeAlice!.TelegramUserId.Should().Be(100);

        var globexAlice = await _registry.GetByAliasAsync("@alice", "globex", default);
        globexAlice!.TelegramUserId.Should().Be(200);
    }

    [Fact]
    public async Task MultiWorkspaceBindings_AreReturnedTogether()
    {
        // Brief Test Scenario: "Multi-workspace bindings returned —
        // Given Telegram user 12345 in chat 67890 has active bindings
        // in workspaces ws-alpha and ws-beta, When ... AuthorizeAsync
        // is called for a non-/start command, Then ... Bindings
        // contains both."
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice-alpha"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-beta",
                alias: "@alice-beta"),
            default);

        var bindings = await _registry.GetBindingsAsync(12345, 67890, default);
        bindings.Select(b => b.WorkspaceId).Should().BeEquivalentTo(new[] { "ws-alpha", "ws-beta" });
    }

    [Fact]
    public async Task UniqueIndex_OnUserChatWorkspace_PreventsDuplicateInsert()
    {
        // Direct DbContext insert of a second row with the same
        // (UserId, ChatId, WorkspaceId) must be rejected by the
        // unique index.
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-alpha"),
            default);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        db.OperatorBindings.Add(new OperatorBinding
        {
            Id = Guid.NewGuid(),
            TelegramUserId = 12345,
            TelegramChatId = 67890,
            ChatType = ChatType.Private,
            OperatorAlias = "@duplicate",
            TenantId = "t-1",
            WorkspaceId = "ws-alpha",
            Roles = Array.Empty<string>(),
            RegisteredAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        var act = () => db.SaveChangesAsync();
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException!.Message.Should().Contain("UNIQUE",
            "ux_operator_bindings_user_chat_workspace must reject a second row for the same (user, chat, workspace) triple — SQLite surfaces UNIQUE violations on the inner SqliteException");
    }

    [Fact]
    public async Task UniqueIndex_OnAliasAndTenant_PreventsCrossWorkspaceDuplicate()
    {
        // The (OperatorAlias, TenantId) index is UNIQUE — two
        // bindings cannot share the same alias within a single
        // tenant even when their workspaces differ.
        await _registry.RegisterAsync(NewRegistration(
                userId: 100, chatId: 200, tenantId: "acme", workspaceId: "ws-alpha",
                alias: "@alice"),
            default);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        db.OperatorBindings.Add(new OperatorBinding
        {
            Id = Guid.NewGuid(),
            TelegramUserId = 200,
            TelegramChatId = 300,
            ChatType = ChatType.Private,
            OperatorAlias = "@alice",
            TenantId = "acme",
            WorkspaceId = "ws-different",
            Roles = Array.Empty<string>(),
            RegisteredAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        var act = () => db.SaveChangesAsync();
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException!.Message.Should().Contain("UNIQUE",
            "ux_operator_bindings_alias_tenant must reject two bindings sharing the same alias within one tenant — SQLite surfaces UNIQUE violations on the inner SqliteException");
    }

    [Fact]
    public async Task GetAllBindingsAsync_ReturnsBindingsAcrossChats()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 1, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@a"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 2, tenantId: "t-1", workspaceId: "ws-beta",
                alias: "@b"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 99999, chatId: 3, tenantId: "t-1", workspaceId: "ws-gamma",
                alias: "@c"),
            default);

        var all = await _registry.GetAllBindingsAsync(12345, default);
        all.Select(b => b.TelegramChatId).Should().BeEquivalentTo(new[] { 1L, 2L });

        var other = await _registry.GetAllBindingsAsync(99999, default);
        other.Should().ContainSingle();
    }

    [Fact]
    public async Task GetByWorkspaceAsync_ReturnsActiveBindingsForWorkspace()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 100, chatId: 1, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 200, chatId: 2, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@bob"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 300, chatId: 3, tenantId: "t-1", workspaceId: "ws-beta",
                alias: "@carol"),
            default);

        var alpha = await _registry.GetByWorkspaceAsync("ws-alpha", default);
        alpha.Select(b => b.OperatorAlias).Should().BeEquivalentTo(new[] { "@alice", "@bob" });
    }

    [Fact]
    public async Task GetActiveTenantsAsync_ReturnsDistinctActiveTenantIds()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 100, chatId: 1, tenantId: "acme", workspaceId: "ws-1",
                alias: "@a"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 200, chatId: 2, tenantId: "acme", workspaceId: "ws-2",
                alias: "@b"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 300, chatId: 3, tenantId: "globex", workspaceId: "ws-1",
                alias: "@c"),
            default);

        var tenants = await _registry.GetActiveTenantsAsync(default);
        tenants.Should().BeEquivalentTo(new[] { "acme", "globex" });
    }

    [Fact]
    public async Task GetByTenantAsync_ReturnsAllActiveBindingsInTenant()
    {
        await _registry.RegisterAsync(NewRegistration(
                userId: 100, chatId: 1, tenantId: "acme", workspaceId: "ws-1",
                alias: "@a"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 200, chatId: 2, tenantId: "acme", workspaceId: "ws-2",
                alias: "@b"),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 300, chatId: 3, tenantId: "globex", workspaceId: "ws-1",
                alias: "@c"),
            default);

        var acme = await _registry.GetByTenantAsync("acme", default);
        acme.Select(b => b.OperatorAlias).Should().BeEquivalentTo(new[] { "@a", "@b" });
    }

    [Fact]
    public void DeriveBindingId_IsDeterministic_AcrossInvocations()
    {
        var first = PersistentOperatorRegistry.DeriveBindingId(100, 200, "acme", "ws-1");
        var second = PersistentOperatorRegistry.DeriveBindingId(100, 200, "acme", "ws-1");
        first.Should().Be(second);

        var diffWorkspace = PersistentOperatorRegistry.DeriveBindingId(100, 200, "acme", "ws-2");
        first.Should().NotBe(diffWorkspace);

        var diffTenant = PersistentOperatorRegistry.DeriveBindingId(100, 200, "globex", "ws-1");
        first.Should().NotBe(diffTenant);
    }

    [Fact]
    public async Task RegisterAsync_MultiWorkspace_InsertsOneRowPerWorkspace()
    {
        // The /start handler iterates UserTenantMappings entries and
        // calls RegisterAsync once per workspace; the persistent
        // store must accept N parallel inserts for the same
        // (user, chat) pair under N distinct workspace ids.
        await _registry.RegisterAsync(NewRegistration(
                userId: 67890, chatId: 11111, tenantId: "acme", workspaceId: "factory-2",
                alias: "@bob-f2", roles: new[] { "Operator" }),
            default);
        await _registry.RegisterAsync(NewRegistration(
                userId: 67890, chatId: 11111, tenantId: "acme", workspaceId: "factory-3",
                alias: "@bob-f3", roles: new[] { "Operator" }),
            default);

        var bindings = await _registry.GetBindingsAsync(67890, 11111, default);
        bindings.Should().HaveCount(2);
        bindings.Select(b => b.WorkspaceId).Should().BeEquivalentTo(new[] { "factory-2", "factory-3" });
    }

    [Fact]
    public async Task GetBindingsAsync_WithCorruptRolesJson_ThrowsInvalidOperationException()
    {
        // Iter-2 evaluator item 4 — corrupt persisted authorization
        // data must surface as a fail-fast error rather than silently
        // coerce to an empty role list (which would either downgrade
        // the operator's privileges or, if downstream code uses
        // "empty list ⇒ wildcard" semantics, silently elevate them).
        // We insert a valid binding via the registry, then directly
        // corrupt the Roles JSON column via raw SQL to simulate
        // hand-edited / partially-migrated rows.
        await _registry.RegisterAsync(NewRegistration(
                userId: 12345, chatId: 67890, tenantId: "t-1", workspaceId: "ws-prod",
                alias: "@alice", roles: new[] { "Operator", "Approver" }),
            default);

        // Corrupt the Roles column directly. The pipeline must throw
        // when the next SELECT materialises this row.
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE operator_bindings SET \"Roles\" = '{not valid json' WHERE \"TelegramUserId\" = 12345";
            await cmd.ExecuteNonQueryAsync();
        }

        Func<Task> act = () => _registry.GetBindingsAsync(12345, 67890, default);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>(
            "iter-2 evaluator item 4: corrupt Roles JSON must surface as a fail-fast error rather than silently demote the operator to an empty role list");
        ex.Which.Message.Should().Contain("operator_bindings.Roles");
        ex.Which.InnerException.Should().BeAssignableTo<System.Text.Json.JsonException>(
            "the original JsonException must be preserved on InnerException so the operator can diagnose the malformed payload");
    }

    [Fact]
    public async Task RegisterManyAsync_RollsBackAllInsertsOnConstraintViolation()
    {
        // Iter-3 evaluator item 2 — atomic batch upsert. The previous
        // OnboardAsync impl iterated RegisterAsync one row at a time;
        // if entry [N] failed on a unique-index violation, earlier
        // rows remained inserted, leaving the operator in exactly
        // the partial-onboarding state the iter-2 blank-field fail-
        // fast was meant to prevent. RegisterManyAsync wraps all
        // upserts in a single transaction — every insert must be
        // rolled back when any row trips a constraint.
        //
        // Pre-seed an existing binding under user 11111 with alias
        // @taken in tenant t-1. Then call RegisterManyAsync for a
        // DIFFERENT user (user 22222) with TWO entries: the first
        // (ws-alpha + @safe) is valid; the second (ws-beta + @taken)
        // collides with the pre-seeded row on the UNIQUE
        // (OperatorAlias, TenantId) index. The batch must throw AND
        // leave NO new rows behind for user 22222.
        await _registry.RegisterAsync(NewRegistration(
                userId: 11111, chatId: 99999, tenantId: "t-1", workspaceId: "ws-existing",
                alias: "@taken", roles: new[] { "Operator" }),
            default);

        var conflicting = new List<OperatorRegistration>
        {
            NewRegistration(
                userId: 22222, chatId: 33333, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@safe", roles: new[] { "Operator" }),
            NewRegistration(
                userId: 22222, chatId: 33333, tenantId: "t-1", workspaceId: "ws-beta",
                alias: "@taken", roles: new[] { "Operator" }),
        };

        Func<Task> act = () => _registry.RegisterManyAsync(conflicting, default);

        await act.Should().ThrowAsync<DbUpdateException>(
            "the second registration collides with the pre-seeded @taken alias in tenant t-1");

        var rolledBackUserBindings = await _registry.GetBindingsAsync(22222, 33333, default);
        rolledBackUserBindings.Should().BeEmpty(
            "iter-3 evaluator item 2: the entire batch must roll back atomically — no partial rows for user 22222 may remain after the constraint violation");

        // The pre-seeded row must still be there — the transaction
        // rollback is scoped to RegisterManyAsync's batch and must
        // not touch unrelated bindings.
        var preexisting = await _registry.GetBindingsAsync(11111, 99999, default);
        preexisting.Should().HaveCount(1);
        preexisting[0].OperatorAlias.Should().Be("@taken");
    }

    [Fact]
    public async Task RegisterManyAsync_InsertsAllRows_WhenBatchHasNoConflicts()
    {
        // Iter-3 evaluator item 2 (positive case) — the happy path:
        // multi-workspace onboarding with no collisions inserts EVERY
        // row in the batch under one transaction. Ensures the
        // transactional wrapper hasn't accidentally suppressed
        // commits on the success path.
        var registrations = new List<OperatorRegistration>
        {
            NewRegistration(
                userId: 55555, chatId: 66666, tenantId: "t-1", workspaceId: "ws-alpha",
                alias: "@alice-alpha", roles: new[] { "Operator" }),
            NewRegistration(
                userId: 55555, chatId: 66666, tenantId: "t-1", workspaceId: "ws-beta",
                alias: "@alice-beta", roles: new[] { "Operator", "Approver" }),
        };

        await _registry.RegisterManyAsync(registrations, default);

        var bindings = await _registry.GetBindingsAsync(55555, 66666, default);
        bindings.Should().HaveCount(2);
        bindings.Select(b => b.WorkspaceId).Should().BeEquivalentTo(new[] { "ws-alpha", "ws-beta" });
        bindings.Select(b => b.OperatorAlias).Should().BeEquivalentTo(new[] { "@alice-alpha", "@alice-beta" });
    }

    [Fact]
    public async Task RegisterManyAsync_EmptyBatch_IsNoOp()
    {
        // Iter-3 evaluator item 2 — defensive: an empty batch must
        // not crash and must not start a transaction. The /start
        // onboarding loop guards against this upstream but the
        // registry contract must be robust to it.
        await _registry.RegisterManyAsync(Array.Empty<OperatorRegistration>(), default);

        var allBindings = await _registry.GetAllBindingsAsync(99999999, default);
        allBindings.Should().BeEmpty();
    }

    private static OperatorRegistration NewRegistration(
        long userId,
        long chatId,
        string tenantId,
        string workspaceId,
        string? alias = null,
        string[]? roles = null) => new()
    {
        TelegramUserId = userId,
        TelegramChatId = chatId,
        ChatType = ChatType.Private,
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        OperatorAlias = alias ?? ("@user-" + userId),
        Roles = roles ?? Array.Empty<string>(),
    };
}
