using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Auth;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.4 — pins the brief's five Test Scenarios for
/// <see cref="TelegramUserAuthorizationService"/> (the production
/// <see cref="IUserAuthorizationService"/> that supersedes the
/// iter-5 <see cref="ConfiguredOperatorAuthorizationService"/>).
/// The service is exercised against the REAL
/// <see cref="PersistentOperatorRegistry"/> + an in-memory SQLite
/// connection so the upsert, restart-persistence, and tenant-scoped
/// alias paths all run against the production schema.
/// </summary>
public sealed class TelegramUserAuthorizationServiceTests : IAsyncLifetime
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

    private TelegramUserAuthorizationService NewService(TelegramOptions options) =>
        new(_registry, new StaticOptionsMonitor<TelegramOptions>(options),
            NullLogger<TelegramUserAuthorizationService>.Instance);

    [Fact]
    public async Task Scenario_AuthorizedUserMapped_StartReturnsAuthorizedWithBinding()
    {
        // Brief Test Scenario: "Authorized user mapped — Given
        // Telegram user 12345 is in the allowlist mapped to
        // operator op-1 in tenant t-1, When /start is received,
        // Then AuthorizationResult.IsAuthorized is true and
        // OperatorId is op-1." (We map the brief's "OperatorId"
        // to OperatorAlias because that is the persisted handle
        // per architecture.md §3.1 / OperatorBinding.OperatorAlias.)
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-prod",
                        Roles = new List<string> { "Operator" },
                        OperatorAlias = "op-1",
                    },
                },
            },
        });

        var result = await svc.AuthorizeAsync(
            externalUserId: "12345",
            chatId: "67890",
            commandName: "/start",
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.DenialReason.Should().BeNull();
        result.Bindings.Should().HaveCount(1);

        var binding = result.Bindings[0];
        binding.TelegramUserId.Should().Be(12345);
        binding.TelegramChatId.Should().Be(67890);
        binding.TenantId.Should().Be("t-1");
        binding.WorkspaceId.Should().Be("ws-prod");
        binding.OperatorAlias.Should().Be("op-1");
        binding.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario_UnauthorizedUserRejected_DenialReasonPopulated()
    {
        // Brief Test Scenario: "Unauthorized user rejected —
        // Given Telegram user 99999 is not in the allowlist,
        // When any command is received, Then ...IsAuthorized is
        // false and DenialReason is populated."
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>(),
        });

        var result = await svc.AuthorizeAsync(
            externalUserId: "99999",
            chatId: "67890",
            commandName: "/start",
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().NotBeNullOrWhiteSpace();
        result.Bindings.Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario_NonStartCommand_FromUnknownUserChatPair_Denied()
    {
        // Tier 2 path: a /status from a user who has never run
        // /start has no OperatorBinding row and must be denied.
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 99999L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>(),
        });

        var result = await svc.AuthorizeAsync(
            externalUserId: "99999",
            chatId: "11111",
            commandName: "/status",
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Scenario_BindingPersistsAcrossRestart_StatusAuthorizedAfterServiceRestart()
    {
        // Brief Test Scenario: "Binding persists across restart —
        // Given Telegram user 12345 sends /start and an
        // OperatorBinding row is created, When the service
        // restarts and user 12345 sends /status from the same
        // chat, Then IsAuthorizedAsync returns true because the
        // binding is persisted in the database."
        var optionsBefore = new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-prod",
                        OperatorAlias = "op-1",
                    },
                },
            },
        };

        var svcBefore = NewService(optionsBefore);
        var startResult = await svcBefore.AuthorizeAsync(
            "12345", "67890", "/start", CancellationToken.None);
        startResult.IsAuthorized.Should().BeTrue();

        // "Restart" — build a brand-new authz service over the same
        // persistent registry (same SqliteConnection). The new
        // service has an EMPTY UserTenantMappings on purpose: the
        // restart must NOT rely on configuration to re-authorize —
        // only on the persistent OperatorBinding row.
        var svcAfter = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>(),
        });

        var statusResult = await svcAfter.AuthorizeAsync(
            "12345", "67890", "/status", CancellationToken.None);

        statusResult.IsAuthorized.Should().BeTrue(
            "the OperatorBinding row was persisted by /start, so the post-restart /status must authorize from the registry without re-reading UserTenantMappings");
        statusResult.Bindings.Should().HaveCount(1);
        statusResult.Bindings[0].OperatorAlias.Should().Be("op-1");
    }

    [Fact]
    public async Task Scenario_AliasLookupResolvesWithinTenant_NullCrossTenant()
    {
        // Brief Test Scenario: "Alias lookup resolves binding
        // within tenant — Given an OperatorBinding exists with
        // OperatorAlias=@operator-1 in TenantId=acme, When
        // GetByAliasAsync(\"@operator-1\", \"acme\") is called,
        // Then the correct OperatorBinding is returned ...; when
        // GetByAliasAsync(\"@operator-1\", \"other-tenant\") is
        // called, Then null is returned because alias resolution
        // is tenant-scoped per architecture.md lines 116–119."
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 100L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["100"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "acme",
                        WorkspaceId = "ws-prod",
                        OperatorAlias = "@operator-1",
                    },
                },
            },
        });

        await svc.AuthorizeAsync("100", "200", "/start", CancellationToken.None);

        var inTenant = await _registry.GetByAliasAsync(
            "@operator-1", "acme", CancellationToken.None);
        inTenant.Should().NotBeNull();
        inTenant!.TelegramUserId.Should().Be(100);
        inTenant.TelegramChatId.Should().Be(200);

        var crossTenant = await _registry.GetByAliasAsync(
            "@operator-1", "other-tenant", CancellationToken.None);
        crossTenant.Should().BeNull(
            "alias resolution must be tenant-scoped (architecture.md lines 116-119) — @operator-1 in tenant acme MUST NOT resolve in tenant other-tenant");
    }

    [Fact]
    public async Task Scenario_MultiWorkspaceBindingsReturned_ContainsBothBindings()
    {
        // Brief Test Scenario: "Multi-workspace bindings returned —
        // Given Telegram user 12345 in chat 67890 has active
        // bindings in workspaces ws-alpha and ws-beta, When
        // TelegramUserAuthorizationService.AuthorizeAsync is called
        // for a non-/start command, Then ...IsAuthorized is true
        // and ...Bindings contains both OperatorBinding records
        // (one for ws-alpha, one for ws-beta) so the pipeline can
        // prompt for workspace disambiguation via inline keyboard."
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@alice-alpha",
                    },
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-beta",
                        OperatorAlias = "@alice-beta",
                    },
                },
            },
        });

        // First onboard via /start so both workspace bindings
        // materialise.
        var startResult = await svc.AuthorizeAsync(
            "12345", "67890", "/start", CancellationToken.None);
        startResult.IsAuthorized.Should().BeTrue();
        startResult.Bindings.Should().HaveCount(2,
            "/start must create one OperatorBinding per UserTenantMappings entry (one per workspace)");

        // Now the non-/start command — this is the test scenario.
        var statusResult = await svc.AuthorizeAsync(
            "12345", "67890", "/status", CancellationToken.None);

        statusResult.IsAuthorized.Should().BeTrue();
        statusResult.Bindings.Should().HaveCount(2);
        statusResult.Bindings.Select(b => b.WorkspaceId)
            .Should().BeEquivalentTo(new[] { "ws-alpha", "ws-beta" });
    }

    [Fact]
    public async Task Authorize_StartFromAllowlistedUserWithNoMapping_Denied()
    {
        // Defensive: allowlist permits onboarding but no
        // UserTenantMappings entry means the operator cannot be
        // assigned a tenant/workspace — silent fabrication would
        // violate the architecture.md §7.1 "all required fields
        // populated" contract.
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>(),
        });

        var result = await svc.AuthorizeAsync(
            "12345", "67890", "/start", CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("UserTenantMappings");
    }

    [Fact]
    public async Task Authorize_NonIntegerUserId_Denied()
    {
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long>(),
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>(),
        });

        var result = await svc.AuthorizeAsync(
            "not-a-number", "67890", "/status", CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Authorize_NonIntegerChatId_Denied()
    {
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long>(),
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>(),
        });

        var result = await svc.AuthorizeAsync(
            "12345", "not-a-chat-id", "/status", CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Authorize_StartReplay_IsIdempotent_DoesNotDuplicateBindings()
    {
        // Replay of /start must NOT create duplicate
        // OperatorBindings — the upsert in PersistentOperatorRegistry
        // is what guarantees this, but the authz service is the
        // entry point so we exercise it end-to-end here.
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-prod",
                        OperatorAlias = "@alice",
                    },
                },
            },
        });

        for (var i = 0; i < 3; i++)
        {
            var result = await svc.AuthorizeAsync(
                "12345", "67890", "/start", CancellationToken.None);
            result.IsAuthorized.Should().BeTrue();
            result.Bindings.Should().HaveCount(1,
                "replays of /start must NOT create duplicate OperatorBindings (the persistent upsert is idempotent)");
        }
    }

    [Fact]
    public async Task Authorize_PassesCancellationTokenThrough()
    {
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long>(),
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>(),
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A cancelled token on Tier 2 must propagate through the
        // registry SELECT — the operation should throw rather
        // than silently authorize (or silently deny without
        // executing the query).
        Func<Task> act = () => svc.AuthorizeAsync(
            "12345", "67890", "/status", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ============================================================
    // Stage 3.4 iter-2 evaluator item 1 — OnboardAsync ChatType
    // ============================================================

    [Theory]
    [InlineData("private", ChatType.Private)]
    [InlineData("group", ChatType.Group)]
    [InlineData("supergroup", ChatType.Supergroup)]
    [InlineData("channel", ChatType.Supergroup)]
    public async Task OnboardAsync_RecordsChatTypeFromTelegramUpdate(string raw, ChatType expected)
    {
        // Iter-2 evaluator item 1 — the new OnboardAsync entry point
        // plumbs the real Telegram chat-type token through to the
        // persisted OperatorBinding.ChatType so group / supergroup /
        // channel onboardings no longer collapse to Private.
        var svc = NewService(BuildAllowedOptions());

        var result = await svc.OnboardAsync(
            externalUserId: "12345",
            chatId: "67890",
            chatType: raw,
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings.Should().HaveCount(1);
        result.Bindings[0].ChatType.Should().Be(expected,
            $"the raw Telegram chat-type token '{raw}' must map to {expected} on the persisted binding");
    }

    [Fact]
    public async Task OnboardAsync_NullChatType_DefaultsToPrivate()
    {
        // Backward-compat: connectors that have not been updated to
        // populate MessengerEvent.ChatType pass null; the parser must
        // default to ChatType.Private to preserve the pre-Stage-3.4
        // behaviour.
        var svc = NewService(BuildAllowedOptions());

        var result = await svc.OnboardAsync(
            externalUserId: "12345",
            chatId: "67890",
            chatType: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings[0].ChatType.Should().Be(ChatType.Private);
    }

    [Fact]
    public async Task OnboardAsync_UnrecognizedChatType_DefaultsToPrivate()
    {
        // Forward-compat: any future Telegram chat-type token that
        // isn't in the conversion table also falls back to Private
        // rather than throwing — operators get onboarded, the
        // misclassification is logged at the mapper level.
        var svc = NewService(BuildAllowedOptions());

        var result = await svc.OnboardAsync(
            externalUserId: "12345",
            chatId: "67890",
            chatType: "future-chat-kind",
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings[0].ChatType.Should().Be(ChatType.Private);
    }

    [Fact]
    public async Task OnboardAsync_LegacyAuthorizeAsync_DefaultsToPrivate()
    {
        // The pinned AuthorizeAsync signature does NOT carry chat
        // type; calling it directly for /start (older callers,
        // contract tests, the default-interface-method fallback in
        // IUserAuthorizationService) must default to Private to
        // match the historical ConfiguredOperatorAuthorizationService
        // behaviour.
        var svc = NewService(BuildAllowedOptions());

        var result = await svc.AuthorizeAsync(
            externalUserId: "12345",
            chatId: "67890",
            commandName: "/start",
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings[0].ChatType.Should().Be(ChatType.Private);
    }

    // ============================================================
    // Stage 3.4 iter-2 evaluator item 3 — partial-mapping fail-fast
    // ============================================================

    [Fact]
    public async Task OnboardAsync_PartialMapping_FailsFast_DoesNotAuthorizeWithSurvivingEntries()
    {
        // Iter-2 evaluator item 3 — a multi-workspace mapping with
        // one valid entry and one entry with a blank WorkspaceId
        // MUST deny the entire /start. The previous "skip invalid
        // entries" behaviour authorized the operator with FEWER
        // bindings than configured, silently demoting them out of
        // the broken workspace.
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@alice",
                    },
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = string.Empty,
                        OperatorAlias = "@alice",
                    },
                },
            },
        });

        var result = await svc.OnboardAsync(
            "12345", "67890", "private", CancellationToken.None);

        result.IsAuthorized.Should().BeFalse(
            "a partially-invalid mapping must fail the entire /start, not authorize the survivors");
        result.DenialReason.Should().NotBeNullOrWhiteSpace();
        result.Bindings.Should().BeEmpty();

        // Defense-in-depth: nothing in the registry was inserted —
        // the fail-fast check runs BEFORE any RegisterAsync call so
        // the database is not left half-populated.
        var stored = await _registry.GetBindingsAsync(12345, 67890, CancellationToken.None);
        stored.Should().BeEmpty(
            "fail-fast must run before any RegisterAsync — otherwise a retried /start would leave a partial binding set");
    }

    private static TelegramOptions BuildAllowedOptions() => new()
    {
        AllowedUserIds = new List<long> { 12345L },
        UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
        {
            ["12345"] = new()
            {
                new TelegramUserTenantMapping
                {
                    TenantId = "t-1",
                    WorkspaceId = "ws-prod",
                    OperatorAlias = "@alice",
                },
            },
        },
    };

    // ============================================================
    // Stage 3.4 iter-5 evaluator item 2 — fail-closed allowlist
    // ============================================================

    [Fact]
    public async Task Onboard_DeniesEmptyAllowlist_WhenRequireAllowlistForOnboardingIsTrue()
    {
        // Iter-5 evaluator item 2 — the brief mandates "checks the
        // allowlist first". An empty allowlist with the default
        // RequireAllowlistForOnboarding=true must FAIL CLOSED:
        // /start is rejected even for users that would otherwise
        // pass the (now-irrelevant) UserTenantMappings lookup,
        // because a production deployment that forgets to populate
        // AllowedUserIds would otherwise silently authorise every
        // Telegram user who DMs the bot.
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long>(),
            RequireAllowlistForOnboarding = true,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-prod",
                        OperatorAlias = "@alice",
                    },
                },
            },
        });

        var result = await svc.AuthorizeAsync(
            "12345", "67890", "/start", CancellationToken.None);

        result.IsAuthorized.Should().BeFalse(
            "an empty AllowedUserIds list under the fail-closed default must deny /start even if a UserTenantMappings entry exists");
        result.DenialReason.Should().Contain("AllowedUserIds");
        result.DenialReason.Should().Contain("RequireAllowlistForOnboarding");
        result.Bindings.Should().BeEmpty();

        // Defense-in-depth: the fail-closed gate runs BEFORE any
        // registry write so no row leaks into operator_bindings.
        var stored = await _registry.GetBindingsAsync(12345, 67890, CancellationToken.None);
        stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Onboard_AllowsEmptyAllowlist_WhenRequireAllowlistForOnboardingIsFalse()
    {
        // Iter-5 evaluator item 2 (negative case) — dev / integration-
        // test fixtures that explicitly opt out of the fail-closed
        // default by setting RequireAllowlistForOnboarding=false get
        // the prior open-by-default behaviour. The UserTenantMappings
        // lookup remains authoritative for who actually gets a
        // binding (a user with no mapping is still denied later), but
        // the empty-allowlist gate alone does not block /start.
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long>(),
            RequireAllowlistForOnboarding = false,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-prod",
                        OperatorAlias = "@alice",
                    },
                },
            },
        });

        var result = await svc.AuthorizeAsync(
            "12345", "67890", "/start", CancellationToken.None);

        result.IsAuthorized.Should().BeTrue(
            "with RequireAllowlistForOnboarding=false, an empty allowlist falls through to UserTenantMappings and authorises if a mapping exists");
        result.Bindings.Should().HaveCount(1);
    }

    [Fact]
    public async Task Onboard_DeniesUnlistedUser_WhenAllowlistPopulated()
    {
        // Iter-5 evaluator item 2 (regression case) — when the
        // allowlist IS populated, an unlisted user is denied
        // regardless of RequireAllowlistForOnboarding. The new
        // setting only changes the EMPTY-allowlist semantics.
        var svc = NewService(new TelegramOptions
        {
            AllowedUserIds = new List<long> { 12345L },
            RequireAllowlistForOnboarding = false,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["99999"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-prod",
                        OperatorAlias = "@intruder",
                    },
                },
            },
        });

        var result = await svc.AuthorizeAsync(
            "99999", "67890", "/start", CancellationToken.None);

        result.IsAuthorized.Should().BeFalse(
            "a populated allowlist that does NOT contain the inbound user must deny, regardless of RequireAllowlistForOnboarding");
        result.DenialReason.Should().Contain("99999");
        result.DenialReason.Should().Contain("AllowedUserIds");
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
