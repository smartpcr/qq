using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Auth;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Iter-5 evaluator item 1 — the runnable host's default
/// <see cref="IUserAuthorizationService"/>
/// (<see cref="ConfiguredOperatorAuthorizationService"/>) must validate
/// BOTH the inbound Telegram user id AND chat id, and surface the
/// configured <see cref="TelegramOperatorBindingOptions.TenantId"/> /
/// <see cref="TelegramOperatorBindingOptions.WorkspaceId"/> on the
/// returned <see cref="OperatorBinding"/>. The iter-4 stub
/// (<c>AllowlistUserAuthorizationService</c>) consulted only
/// <see cref="TelegramOptions.AllowedUserIds"/> and fabricated
/// <c>"default"</c> tenant/workspace; this suite pins the new
/// per-binding contract.
/// </summary>
public sealed class ConfiguredOperatorAuthorizationServiceTests
{
    private const long AllowedUser = 12345L;
    private const long AllowedChat = 67890L;
    private const long OtherChat = 11111L;
    private const long OtherUser = 22222L;

    private static ConfiguredOperatorAuthorizationService NewService(
        TelegramOptions options) =>
        new(new StaticOptionsMonitor<TelegramOptions>(options));

    private static TelegramOptions OptionsWith(
        IEnumerable<TelegramOperatorBindingOptions>? bindings = null,
        IEnumerable<long>? allowedUserIds = null) => new()
    {
        AllowedUserIds = allowedUserIds is null ? new List<long>() : new List<long>(allowedUserIds),
        OperatorBindings = bindings is null
            ? new List<TelegramOperatorBindingOptions>()
            : new List<TelegramOperatorBindingOptions>(bindings),
    };

    private static TelegramOperatorBindingOptions Binding(
        long userId, long chatId, string tenant = "acme", string workspace = "swarm-prod",
        string? alias = null, string[]? roles = null) => new()
    {
        TelegramUserId = userId,
        TelegramChatId = chatId,
        TenantId = tenant,
        WorkspaceId = workspace,
        OperatorAlias = alias,
        Roles = roles is null ? new List<string>() : new List<string>(roles),
    };

    [Fact]
    public async Task Authorize_BindingMatchesUserAndChat_ReturnsAuthorizedWithBindingTenantWorkspace()
    {
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat, tenant: "acme", workspace: "swarm-prod",
                alias: "alice", roles: new[] { "operator", "approver" }),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: "status",
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.DenialReason.Should().BeNull();
        result.Bindings.Should().HaveCount(1);
        var binding = result.Bindings[0];
        binding.TelegramUserId.Should().Be(AllowedUser);
        binding.TelegramChatId.Should().Be(AllowedChat);
        binding.TenantId.Should().Be("acme",
            "tenant must come from the binding entry, not a hardcoded 'default'");
        binding.WorkspaceId.Should().Be("swarm-prod",
            "workspace must come from the binding entry, not a hardcoded 'default'");
        binding.OperatorAlias.Should().Be("alice");
        binding.Roles.Should().BeEquivalentTo(new[] { "operator", "approver" });
        binding.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Authorize_AllowedUserButWrongChat_ReturnsUnauthorized()
    {
        // Pinning the "Validate chat/user allowlist" requirement: an
        // allowlisted user on an UNAUTHORIZED chat must be denied.
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: OtherChat.ToString(),
            commandName: "status",
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Bindings.Should().BeEmpty();
        result.DenialReason.Should().Contain("OperatorBindings")
            .And.Contain(AllowedUser.ToString())
            .And.Contain(OtherChat.ToString());
    }

    [Fact]
    public async Task Authorize_UnknownUserOnAuthorizedChat_ReturnsUnauthorized()
    {
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: OtherUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("OperatorBindings");
    }

    [Fact]
    public async Task Authorize_NoBindingsConfigured_ReturnsUnauthorized()
    {
        var svc = NewService(OptionsWith(/* no bindings */));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("OperatorBindings is empty");
    }

    [Fact]
    public async Task Authorize_AllowedUserIdsGate_RejectsUserNotInList()
    {
        // Coarse user-id gate: even with a matching binding, a user
        // not in AllowedUserIds (when the list is non-empty) is denied.
        var svc = NewService(OptionsWith(
            bindings: new[] { Binding(AllowedUser, AllowedChat) },
            allowedUserIds: new[] { 99999L })); // AllowedUser NOT in list

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("AllowedUserIds");
    }

    [Fact]
    public async Task Authorize_AllowedUserIdsEmpty_DoesNotGate()
    {
        // Empty AllowedUserIds means "no extra gate"; bindings alone
        // are the source of truth.
        var svc = NewService(OptionsWith(
            bindings: new[] { Binding(AllowedUser, AllowedChat) },
            allowedUserIds: Array.Empty<long>()));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
    }

    [Fact]
    public async Task Authorize_NonNumericExternalUserId_ReturnsUnauthorized()
    {
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: "not-a-number",
            chatId: AllowedChat.ToString(),
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("not a valid Telegram user id");
    }

    [Fact]
    public async Task Authorize_NonNumericChatId_ReturnsUnauthorized()
    {
        // Iter-4 stub silently fell back to user-id-as-chat-id for
        // non-numeric chat ids; that's wrong because the binding
        // lookup key would never legitimately match a synthesized
        // value. iter-5 fix: deny.
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: "channel:special",
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("not a valid Telegram chat id");
    }

    [Fact]
    public async Task Authorize_BlankExternalUserId_ReturnsUnauthorized()
    {
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: "   ",
            chatId: AllowedChat.ToString(),
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("non-empty");
    }

    [Fact]
    public async Task Authorize_BlankChatId_ReturnsUnauthorized()
    {
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: "",
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("chatId");
    }

    [Fact]
    public async Task Authorize_BlankAlias_FallsBackToUserAliasFormat()
    {
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat, alias: null),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings[0].OperatorAlias.Should().Be($"user-{AllowedUser}");
    }

    [Fact]
    public async Task Authorize_MultipleMatchingBindings_ReturnsAllInConfigOrder()
    {
        // Iter-5 evaluator item 1 — when the SAME (user, chat) appears
        // in MULTIPLE OperatorBindings entries (one per workspace), the
        // service MUST return ALL of them so the pipeline's multi-
        // workspace branch can prompt the operator to choose a
        // workspace before dispatching. The previous "first wins"
        // behavior silently routed commands to the wrong workspace and
        // skipped the disambiguation prompt entirely.
        var svc = NewService(OptionsWith(bindings: new[]
        {
            Binding(AllowedUser, AllowedChat, tenant: "first", workspace: "first-ws"),
            Binding(AllowedUser, AllowedChat, tenant: "second", workspace: "second-ws"),
        }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: null,
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings.Should().HaveCount(2,
            "BOTH matching bindings must be returned so the pipeline's multi-workspace branch fires");
        result.Bindings[0].TenantId.Should().Be("first",
            "the returned order MUST match config order so config remains the source of truth");
        result.Bindings[0].WorkspaceId.Should().Be("first-ws");
        result.Bindings[1].TenantId.Should().Be("second");
        result.Bindings[1].WorkspaceId.Should().Be("second-ws");
        result.Bindings[0].Id.Should().NotBe(result.Bindings[1].Id,
            "binding ids must be distinct so the disambiguation prompt can identify each option uniquely");
    }

    [Fact]
    public async Task Authorize_StartCommand_AllowlistedUserWithoutBinding_SynthesizesOnboardingBinding()
    {
        // Iter-5 evaluator item 2 — `/start` is the registration entry
        // point. A user who is in the allowlist but has no binding yet
        // MUST be allowed to invoke `/start` so the registration
        // handler can run. The service synthesizes an onboarding
        // binding carrying sentinel tenant/workspace ids so the
        // pipeline (which requires Bindings.Count > 0) does not deny
        // the request.
        var svc = NewService(OptionsWith(
            bindings: Array.Empty<TelegramOperatorBindingOptions>(),
            allowedUserIds: new[] { AllowedUser }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: "/start",
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings.Should().HaveCount(1,
            "exactly one onboarding binding so the pipeline takes the single-binding path");
        var binding = result.Bindings[0];
        binding.TelegramUserId.Should().Be(AllowedUser);
        binding.TelegramChatId.Should().Be(AllowedChat);
        binding.TenantId.Should().Be(ConfiguredOperatorAuthorizationService.OnboardingTenantId);
        binding.WorkspaceId.Should().Be(ConfiguredOperatorAuthorizationService.OnboardingWorkspaceId);
        binding.IsActive.Should().BeTrue();
        binding.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task Authorize_StartCommand_AllowlistEmpty_StillSynthesizesOnboarding()
    {
        // Open-onboarding configuration (empty allowlist) — `/start`
        // is allowed for anyone, with the registration handler being
        // the next-line gate.
        var svc = NewService(OptionsWith(
            bindings: Array.Empty<TelegramOperatorBindingOptions>(),
            allowedUserIds: Array.Empty<long>()));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: "/start",
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings.Should().HaveCount(1);
        result.Bindings[0].TenantId.Should().Be(
            ConfiguredOperatorAuthorizationService.OnboardingTenantId);
    }

    [Fact]
    public async Task Authorize_StartCommand_AllowlistedUserWithRealBinding_ReturnsRealBindingNotOnboarding()
    {
        // When the user already has a binding, `/start` returns the
        // REAL binding (not the onboarding sentinel). The handler can
        // then decide to re-prompt or treat it as a no-op.
        var svc = NewService(OptionsWith(
            bindings: new[]
            {
                Binding(AllowedUser, AllowedChat, tenant: "acme", workspace: "swarm-prod"),
            },
            allowedUserIds: new[] { AllowedUser }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: "/start",
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Bindings.Should().HaveCount(1);
        result.Bindings[0].TenantId.Should().Be("acme",
            "real bindings take precedence over the onboarding sentinel");
    }

    [Fact]
    public async Task Authorize_StartCommand_UserNotInAllowlist_Denied()
    {
        // Tier-1 gate: a user NOT in a non-empty allowlist cannot use
        // `/start` to bootstrap onboarding. The allowlist remains the
        // primary admission control.
        var svc = NewService(OptionsWith(
            bindings: Array.Empty<TelegramOperatorBindingOptions>(),
            allowedUserIds: new[] { OtherUser })); // AllowedUser NOT in list

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: "/start",
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("AllowedUserIds");
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("start")]
    [InlineData("/START")]
    [InlineData("Start")]
    [InlineData(" /start ")] // upstream may pass trimmed or untrimmed
    public async Task Authorize_StartCommand_CaseInsensitive_AndLeadingSlashTolerant(string commandName)
    {
        var svc = NewService(OptionsWith(
            bindings: Array.Empty<TelegramOperatorBindingOptions>(),
            allowedUserIds: new[] { AllowedUser }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: commandName?.Trim(),
            CancellationToken.None);

        result.IsAuthorized.Should().BeTrue(
            $"`/start` detection must accept '{commandName}'");
        result.Bindings[0].TenantId.Should().Be(
            ConfiguredOperatorAuthorizationService.OnboardingTenantId);
    }

    [Fact]
    public async Task Authorize_NonStartCommand_WithoutBinding_StillDenied()
    {
        // Defense-in-depth: only `/start` triggers onboarding
        // synthesis. Any other command without a binding is denied as
        // before.
        var svc = NewService(OptionsWith(
            bindings: Array.Empty<TelegramOperatorBindingOptions>(),
            allowedUserIds: new[] { AllowedUser }));

        var result = await svc.AuthorizeAsync(
            externalUserId: AllowedUser.ToString(),
            chatId: AllowedChat.ToString(),
            commandName: "status",
            CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.DenialReason.Should().Contain("OperatorBindings is empty");
    }

    [Fact]
    public void DeriveBindingId_IsDeterministic()
    {
        var first = ConfiguredOperatorAuthorizationService.DeriveBindingId(
            AllowedUser, AllowedChat, "acme", "swarm-prod");
        var second = ConfiguredOperatorAuthorizationService.DeriveBindingId(
            AllowedUser, AllowedChat, "acme", "swarm-prod");
        var differentChat = ConfiguredOperatorAuthorizationService.DeriveBindingId(
            AllowedUser, OtherChat, "acme", "swarm-prod");
        var differentUser = ConfiguredOperatorAuthorizationService.DeriveBindingId(
            OtherUser, AllowedChat, "acme", "swarm-prod");
        var differentTenant = ConfiguredOperatorAuthorizationService.DeriveBindingId(
            AllowedUser, AllowedChat, "contoso", "swarm-prod");
        var differentWorkspace = ConfiguredOperatorAuthorizationService.DeriveBindingId(
            AllowedUser, AllowedChat, "acme", "swarm-dev");

        first.Should().Be(second,
            "repeated authorizations for the same (user, chat, tenant, workspace) must produce the same binding id so the audit trail is stable");
        first.Should().NotBe(differentChat,
            "the same user on a different chat must produce a distinct binding id (each (user, chat) pair is its own binding)");
        first.Should().NotBe(differentUser,
            "different users on the same chat must produce distinct binding ids");
        first.Should().NotBe(differentTenant,
            "iter-5 item 1 — bindings differing only by tenant must produce distinct ids so the multi-workspace prompt has unique keys");
        first.Should().NotBe(differentWorkspace,
            "iter-5 item 1 — bindings differing only by workspace must produce distinct ids");
        first.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        var act = () => new ConfiguredOperatorAuthorizationService(null!);
        act.Should().Throw<ArgumentNullException>();
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
