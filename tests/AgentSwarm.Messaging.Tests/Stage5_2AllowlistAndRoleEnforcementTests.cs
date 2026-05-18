using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Auth;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.2 -- Chat and User Allowlist Enforcement.
///
/// Pins the brief's seven Test Scenarios on the already-shipped
/// implementation surface (introduced in earlier stages):
/// <list type="bullet">
///   <item><description>Tier 1 onboarding via <see cref="TelegramOptions.AllowedUserIds"/>
///   on <c>/start</c> only.</description></item>
///   <item><description>Tier 2 runtime authorization via
///   <see cref="IOperatorRegistry.GetBindingsAsync"/> for every other command.</description></item>
///   <item><description>Role enforcement via <see cref="CommandRoleRequirements"/>:
///   <c>/approve</c> + <c>/reject</c> => <c>Approver</c>; <c>/pause</c> + <c>/resume</c> =>
///   <c>Operator</c>; everything else ungated.</description></item>
///   <item><description>Polite denial via <see cref="PipelineResponses.Unauthorized"/> /
///   <see cref="PipelineResponses.InsufficientPermissions"/>; structured Warning log
///   with <c>Stage=authorize-denied</c> or <c>Stage=role-denied</c>.</description></item>
///   <item><description>Dynamic allowlist reload via <see cref="IOptionsMonitor{TOptions}"/>
///   -- the service reads <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on every
///   call, so a configuration provider change flows through without restart.</description></item>
///   <item><description>Multi-workspace disambiguation prompt when more than one
///   <see cref="OperatorBinding"/> is returned by Tier 2.</description></item>
/// </list>
///
/// Coverage is layered: pipeline-level tests with a mocked
/// <see cref="IUserAuthorizationService"/> pin the role enforcement and
/// disambiguation surface; service-level tests with the real
/// <see cref="PersistentOperatorRegistry"/> backed by an in-memory SQLite
/// connection pin the Tier 1/Tier 2 + dynamic-reload semantics.
/// </summary>
public sealed class Stage5_2AllowlistAndRoleEnforcementTests : IAsyncLifetime
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

    // ============================================================
    // Brief Scenario: Allowed user passes -- Given user ID 12345 has an
    // active OperatorBinding for chat ID 67890, When a command arrives
    // from user 12345 in chat 67890, Then processing continues normally.
    // ============================================================

    [Fact]
    public async Task Stage5_2_AllowedUser_WithActiveBinding_AuthorizesTier2Command()
    {
        var options = BuildOptions(
            allowedUserIds: new[] { 12345L },
            mappings: ("12345", new[] { Mapping("t-1", "ws-prod", "@alice", "Operator") }));
        var monitor = new MutableOptionsMonitor<TelegramOptions>(options);
        var svc = new TelegramUserAuthorizationService(
            _registry, monitor, NullLogger<TelegramUserAuthorizationService>.Instance);

        // First /start so an active OperatorBinding exists.
        var startResult = await svc.AuthorizeAsync(
            "12345", "67890", "/start", CancellationToken.None);
        startResult.IsAuthorized.Should().BeTrue(
            "the brief's precondition is that the binding already exists");

        // The non-/start command is the Stage 5.2 scenario under test.
        var statusResult = await svc.AuthorizeAsync(
            "12345", "67890", "/status", CancellationToken.None);

        statusResult.IsAuthorized.Should().BeTrue(
            "an inbound command from a (user, chat) pair that has at least one active OperatorBinding must pass Tier 2 authorization per architecture.md section 7.1");
        statusResult.Bindings.Should().HaveCount(1);
        statusResult.Bindings[0].TelegramUserId.Should().Be(12345L);
        statusResult.Bindings[0].TelegramChatId.Should().Be(67890L);
        statusResult.DenialReason.Should().BeNull();
    }

    // ============================================================
    // Brief Scenario: Denied user blocked -- Given user ID 99999 has no
    // OperatorBinding record, When a command arrives, Then the user
    // receives the denial message and no command handler is invoked.
    // ============================================================

    [Fact]
    public async Task Stage5_2_DeniedUser_WithNoBinding_RejectedAtTier2WithDenialReason()
    {
        var svc = new TelegramUserAuthorizationService(
            _registry,
            new MutableOptionsMonitor<TelegramOptions>(BuildOptions(allowedUserIds: Array.Empty<long>())),
            NullLogger<TelegramUserAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(
            "99999", "67890", "/status", CancellationToken.None);

        result.IsAuthorized.Should().BeFalse(
            "an inbound non-/start command from a (user, chat) pair with NO active OperatorBinding must be rejected at Tier 2 per architecture.md section 7.1");
        result.Bindings.Should().BeEmpty();
        result.DenialReason.Should().NotBeNullOrWhiteSpace(
            "the denial must carry a structured reason so the pipeline can emit a Warning log");
        result.DenialReason.Should().Contain("99999");
        result.DenialReason.Should().Contain("67890");
    }

    // ============================================================
    // Brief Scenario: Chat authorized through /start -- Given user 12345
    // is in TelegramOptions.AllowedUserIds and sends /start from chat
    // 55555, When 12345 later sends /status from chat 55555, Then the
    // command is accepted because the OperatorBinding exists for that
    // (userId, chatId) pair.
    // ============================================================

    [Fact]
    public async Task Stage5_2_ChatAuthorizedThroughStart_SubsequentStatusFromSameChatAccepted()
    {
        var options = BuildOptions(
            allowedUserIds: new[] { 12345L },
            mappings: ("12345", new[] { Mapping("t-1", "ws-prod", "@alice", "Operator") }));
        var monitor = new MutableOptionsMonitor<TelegramOptions>(options);
        var svc = new TelegramUserAuthorizationService(
            _registry, monitor, NullLogger<TelegramUserAuthorizationService>.Instance);

        var startResult = await svc.AuthorizeAsync(
            "12345", "55555", "/start", CancellationToken.None);
        startResult.IsAuthorized.Should().BeTrue();

        // The subsequent /status from the same chat is the Stage 5.2
        // scenario: the (12345, 55555) pair gained an OperatorBinding at
        // /start time and must therefore pass Tier 2 without re-reading
        // the configuration allowlist.
        var statusResult = await svc.AuthorizeAsync(
            "12345", "55555", "/status", CancellationToken.None);

        statusResult.IsAuthorized.Should().BeTrue(
            "/start created the (12345, 55555) OperatorBinding row, so /status from the same chat must pass Tier 2 (architecture.md section 7.1: 'the OperatorBinding table IS the chat/user allowlist')");
        statusResult.Bindings.Should().HaveCount(1);
        statusResult.Bindings[0].TelegramChatId.Should().Be(55555L);

        // Defense-in-depth: the same user from a DIFFERENT chat must
        // still be denied -- chat authorization is per-(user, chat) pair,
        // not per-user.
        var otherChatResult = await svc.AuthorizeAsync(
            "12345", "99999", "/status", CancellationToken.None);
        otherChatResult.IsAuthorized.Should().BeFalse(
            "the OperatorBinding is keyed on (user, chat); a /status from a different chat must NOT be authorised by the binding created for chat 55555");
    }

    // ============================================================
    // Brief Scenario: Dynamic reload -- Given user 67890 is added to
    // Telegram:AllowedUserIds while the service is running, When 67890
    // sends /start, Then the OperatorBinding is created and subsequent
    // commands are accepted without a restart (verified via
    // IOptionsMonitor<TelegramOptions> reload).
    // ============================================================

    [Fact]
    public async Task Stage5_2_DynamicAllowlistReload_NewUserCanStart_WithoutRestart()
    {
        // Initial state: allowlist does NOT include 67890; UserTenantMappings
        // already carries an entry for it (the Stage 3.4 onboarding source of
        // truth) so the only thing changing at "reload time" is the allowlist.
        var before = BuildOptions(
            allowedUserIds: new[] { 12345L },
            mappings: ("67890", new[] { Mapping("t-1", "ws-prod", "@bob", "Operator") }));
        var monitor = new MutableOptionsMonitor<TelegramOptions>(before);
        var svc = new TelegramUserAuthorizationService(
            _registry, monitor, NullLogger<TelegramUserAuthorizationService>.Instance);

        // Pre-reload: 67890 is NOT yet allowed -> /start is denied at Tier 1.
        var preReloadResult = await svc.AuthorizeAsync(
            "67890", "11111", "/start", CancellationToken.None);
        preReloadResult.IsAuthorized.Should().BeFalse(
            "before the reload, user 67890 is not in AllowedUserIds and /start must be denied at Tier 1");
        preReloadResult.DenialReason.Should().Contain("AllowedUserIds");
        (await _registry.GetBindingsAsync(67890, 11111, CancellationToken.None))
            .Should().BeEmpty(
                "the Tier 1 denial must short-circuit BEFORE any OperatorBinding insert");

        // Simulate a configuration reload: TelegramOptions is rebound
        // with 67890 added to AllowedUserIds. The IOptionsMonitor change
        // notification is what the production OptionsMonitor<T> fires on
        // any reloadable IConfiguration provider change.
        var after = BuildOptions(
            allowedUserIds: new[] { 12345L, 67890L },
            mappings: ("67890", new[] { Mapping("t-1", "ws-prod", "@bob", "Operator") }));
        monitor.TriggerChange(after);

        // Post-reload: the same /start now passes Tier 1 and persists
        // a binding -- without recreating the authorization service.
        var postReloadStart = await svc.AuthorizeAsync(
            "67890", "11111", "/start", CancellationToken.None);
        postReloadStart.IsAuthorized.Should().BeTrue(
            "after the IOptionsMonitor reload, user 67890 is now in AllowedUserIds and /start must succeed WITHOUT restarting the authorization service");
        postReloadStart.Bindings.Should().HaveCount(1);

        // Subsequent (Tier 2) commands from the same (user, chat) pair
        // are accepted because the /start onboarding persisted the
        // OperatorBinding into the registry -- the reload's effect is
        // therefore durable across the next request, not just observable
        // on the one /start call.
        var statusAfter = await svc.AuthorizeAsync(
            "67890", "11111", "/status", CancellationToken.None);
        statusAfter.IsAuthorized.Should().BeTrue(
            "Tier 2 authorisation must see the freshly-onboarded binding without a service restart");
    }

    // ============================================================
    // Brief Scenario: Multi-workspace disambiguation on command -- Given
    // user 12345 has active bindings in workspaces ws-alpha and ws-beta
    // for chat 67890, When /status is sent, Then
    // AuthorizationResult.Bindings contains both bindings and the
    // pipeline presents an inline keyboard listing ws-alpha and ws-beta.
    // ============================================================

    [Fact]
    public async Task Stage5_2_MultiWorkspaceDisambiguation_AuthorizeReturnsAllBindings_PipelinePromptsForSelection()
    {
        // Service-level: AuthorizeAsync returns BOTH bindings.
        var options = BuildOptions(
            allowedUserIds: new[] { 12345L },
            mappings: ("12345", new[]
            {
                Mapping("t-1", "ws-alpha", "@alice-alpha", "Operator"),
                Mapping("t-1", "ws-beta", "@alice-beta", "Operator"),
            }));
        var monitor = new MutableOptionsMonitor<TelegramOptions>(options);
        var svc = new TelegramUserAuthorizationService(
            _registry, monitor, NullLogger<TelegramUserAuthorizationService>.Instance);

        var startResult = await svc.AuthorizeAsync(
            "12345", "67890", "/start", CancellationToken.None);
        startResult.IsAuthorized.Should().BeTrue();
        startResult.Bindings.Should().HaveCount(2);

        var statusResult = await svc.AuthorizeAsync(
            "12345", "67890", "/status", CancellationToken.None);
        statusResult.IsAuthorized.Should().BeTrue();
        statusResult.Bindings.Should().HaveCount(2,
            "Tier 2 must surface ALL active bindings so the pipeline can present a workspace-selection prompt");
        statusResult.Bindings.Select(b => b.WorkspaceId)
            .Should().BeEquivalentTo(new[] { "ws-alpha", "ws-beta" });

        // Pipeline-level: the same authz result triggers the
        // workspace-selection prompt with one button per binding.
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(statusResult.Bindings.ToArray());
        harness.SetupCommand(TelegramCommands.Status, "/status");

        var evt = harness.MakeCommand("/status");
        var pipelineResult = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        pipelineResult.Handled.Should().BeTrue();
        pipelineResult.ResponseText.Should().Be(PipelineResponses.MultiWorkspacePromptText,
            "the pipeline must present the disambiguation prompt when Bindings.Count > 1");
        pipelineResult.ResponseButtons.Should().NotBeNull();
        pipelineResult.ResponseButtons!.Should().HaveCount(2,
            "one button per workspace per architecture.md section 4.3 multi-workspace flow");
        pipelineResult.ResponseButtons.Select(b => b.Label)
            .Should().BeEquivalentTo(new[] { "ws-alpha", "ws-beta" });
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the router must NOT receive a command until the operator picks a workspace");
    }

    // ============================================================
    // Brief Scenario: Approve requires Approver role.
    // ============================================================

    [Fact]
    public async Task Stage5_2_Approve_WithoutApproverRole_Rejected_WarningLogged()
    {
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Operator" }));
        harness.SetupCommand(TelegramCommands.Approve, "/approve");

        var evt = harness.MakeCommand("/approve");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        AssertInsufficientPermissions(harness, result, TelegramCommands.Approve, expectedRole: "Approver");
    }

    [Fact]
    public async Task Stage5_2_Approve_WithApproverRole_RoutedToCommandRouter()
    {
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Approver" }));
        harness.SetupCommand(TelegramCommands.Approve, "/approve");
        harness.SetupRouterSuccess("approved");

        var evt = harness.MakeCommand("/approve");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        AssertRouted(harness, result, "approved");
    }

    // ============================================================
    // Brief Scenario: Reject requires Approver role.
    // ============================================================

    [Fact]
    public async Task Stage5_2_Reject_WithoutApproverRole_Rejected_WarningLogged()
    {
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Operator" }));
        harness.SetupCommand(TelegramCommands.Reject, "/reject");

        var evt = harness.MakeCommand("/reject");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        AssertInsufficientPermissions(harness, result, TelegramCommands.Reject, expectedRole: "Approver");
    }

    [Fact]
    public async Task Stage5_2_Reject_WithApproverRole_RoutedToCommandRouter()
    {
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Approver" }));
        harness.SetupCommand(TelegramCommands.Reject, "/reject");
        harness.SetupRouterSuccess("rejected");

        var evt = harness.MakeCommand("/reject");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        AssertRouted(harness, result, "rejected");
    }

    // ============================================================
    // Brief Scenario: Pause requires Operator role.
    // ============================================================

    [Fact]
    public async Task Stage5_2_Pause_WithoutOperatorRole_Rejected_WarningLogged()
    {
        // The negative path: an operator whose binding only carries the
        // Approver role tries /pause -- must be rejected.
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Approver" }));
        harness.SetupCommand(TelegramCommands.Pause, "/pause");

        var evt = harness.MakeCommand("/pause");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        AssertInsufficientPermissions(harness, result, TelegramCommands.Pause, expectedRole: "Operator");
    }

    [Fact]
    public async Task Stage5_2_Pause_WithOperatorRole_RoutedToCommandRouter()
    {
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Operator" }));
        harness.SetupCommand(TelegramCommands.Pause, "/pause");
        harness.SetupRouterSuccess("paused");

        var evt = harness.MakeCommand("/pause");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        AssertRouted(harness, result, "paused");
    }

    // ============================================================
    // Brief Scenario: Resume requires Operator role.
    // ============================================================

    [Fact]
    public async Task Stage5_2_Resume_WithoutOperatorRole_Rejected_WarningLogged()
    {
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Approver" }));
        harness.SetupCommand(TelegramCommands.Resume, "/resume");

        var evt = harness.MakeCommand("/resume");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        AssertInsufficientPermissions(harness, result, TelegramCommands.Resume, expectedRole: "Operator");
    }

    [Fact]
    public async Task Stage5_2_Resume_WithOperatorRole_RoutedToCommandRouter()
    {
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Operator" }));
        harness.SetupCommand(TelegramCommands.Resume, "/resume");
        harness.SetupRouterSuccess("resumed");

        var evt = harness.MakeCommand("/resume");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        AssertRouted(harness, result, "resumed");
    }

    // ============================================================
    // Non role-gated commands pass regardless of which roles the
    // operator holds -- pins the brief's "no role requirement beyond
    // Tier 2 binding authorization" clause for /status, /agents, /ask,
    // /handoff, and /start.
    // ============================================================

    [Theory]
    [InlineData(TelegramCommands.Status, "/status")]
    [InlineData(TelegramCommands.Agents, "/agents")]
    [InlineData(TelegramCommands.Ask, "/ask hello")]
    [InlineData(TelegramCommands.Handoff, "/handoff @bob")]
    public async Task Stage5_2_NonRoleGatedCommand_WithEmptyRoles_RoutedToCommandRouter(
        string commandName, string rawCommand)
    {
        var harness = new RoleEnforcementHarness();
        // No roles attached -- the brief's contract is that these
        // commands have NO role requirement beyond Tier 2 binding.
        harness.AuthorizeWith(harness.MakeBinding(roles: Array.Empty<string>()));
        harness.SetupCommand(commandName, rawCommand);
        harness.SetupRouterSuccess("ok");

        var evt = harness.MakeCommand(rawCommand);
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Succeeded.Should().BeTrue();
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Once,
            $"{commandName} has no role requirement beyond Tier 2 binding, so an operator with empty Roles must still be routed");
    }

    // ============================================================
    // Brief Implementation Step: "If user is not authorized, respond
    // with a polite denial message, log the attempt at Warning level
    // with user ID and chat ID (but no PII beyond Telegram numeric
    // IDs), and short-circuit processing."
    // ============================================================

    [Fact]
    public async Task Stage5_2_Unauthorized_PipelineRepliesWithPoliteDenial_AndEmitsWarningLogWithIds()
    {
        var harness = new RoleEnforcementHarness();
        // Stage 5.2 (iter-3) -- pipeline calls the unified 5-arg
        // AuthorizeAsync overload for every command. Stub it so the
        // empty-bindings path produces the polite-denial reply and
        // the Warning-level audit log.
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                DenialReason = "User 99999 has no active OperatorBinding for chat 11111.",
                Bindings = Array.Empty<OperatorBinding>(),
            });
        // Back-compat: keep the 4-arg AuthorizeAsync and OnboardAsync
        // stubs in case a future regression points the pipeline back
        // at one of those entry points; the test should fail loudly
        // with the same denial regardless of which overload runs.
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                DenialReason = "User 99999 has no active OperatorBinding for chat 11111.",
                Bindings = Array.Empty<OperatorBinding>(),
            });
        harness.AuthzStub.Setup(s => s.OnboardAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                DenialReason = "User 99999 not allow-listed.",
                Bindings = Array.Empty<OperatorBinding>(),
            });
        harness.SetupCommand(TelegramCommands.Status, "/status");

        var evt = harness.MakeCommand("/status");
        evt = evt with { UserId = "99999", ChatId = "11111" };
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue(
            "the pipeline must short-circuit processing, but the rejection itself is a 'handled' outcome");
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized,
            "the polite denial wording is the canonical PipelineResponses.Unauthorized constant");
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no command handler runs for an unauthorized event");

        var deniedEntry = harness.LogCapture.Entries
            .FirstOrDefault(e => e.GetValue<string>("Stage") == "authorize-denied");
        deniedEntry.Should().NotBeNull(
            "the brief's 'log the attempt at Warning level' requirement is satisfied by the pipeline's structured authorize-denied entry");
        deniedEntry!.Level.Should().Be(LogLevel.Warning);
        deniedEntry.GetValue<object>("UserId")?.ToString().Should().Be("99999",
            "the warning log must carry the Telegram numeric user id");
        deniedEntry.GetValue<object>("ChatId")?.ToString().Should().Be("11111",
            "the warning log must carry the Telegram numeric chat id");
    }

    [Fact]
    public async Task Stage5_2_RoleDenied_PipelineEmitsWarningLogWithCommandAndRequiredRole()
    {
        var harness = new RoleEnforcementHarness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Operator" }));
        harness.SetupCommand(TelegramCommands.Approve, "/approve");

        var evt = harness.MakeCommand("/approve");
        await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        var deniedEntry = harness.LogCapture.Entries
            .FirstOrDefault(e => e.GetValue<string>("Stage") == "role-denied");
        deniedEntry.Should().NotBeNull(
            "the brief's role-enforcement 'audit log entry at Warning level' is satisfied by the pipeline's structured role-denied entry");
        deniedEntry!.Level.Should().Be(LogLevel.Warning);
        deniedEntry.GetValue<string>("Command").Should().Be(TelegramCommands.Approve);
        deniedEntry.GetValue<string>("RequiredRole").Should().Be("Approver");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static TelegramUserTenantMapping Mapping(
        string tenantId, string workspaceId, string alias, params string[] roles) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            OperatorAlias = alias,
            Roles = roles.ToList(),
        };

    private static TelegramOptions BuildOptions(
        IReadOnlyList<long> allowedUserIds,
        params (string Key, TelegramUserTenantMapping[] Entries)[] mappings)
    {
        var dict = new Dictionary<string, List<TelegramUserTenantMapping>>();
        foreach (var (key, entries) in mappings)
        {
            dict[key] = entries.ToList();
        }
        return new TelegramOptions
        {
            AllowedUserIds = allowedUserIds.ToList(),
            UserTenantMappings = dict,
        };
    }

    private static void AssertInsufficientPermissions(
        RoleEnforcementHarness harness,
        PipelineResult result,
        string commandName,
        string expectedRole)
    {
        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.InsufficientPermissions,
            $"{commandName} from an operator without the {expectedRole} role must reply with the canonical insufficient-permissions string");
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never,
            $"the router must NOT be invoked when role enforcement rejects {commandName}");
        harness.DedupStub.Verify(
            d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "denied commands must not be marked processed (the reservation stays held to short-circuit live re-deliveries)");

        var deniedEntry = harness.LogCapture.Entries
            .FirstOrDefault(e => e.GetValue<string>("Stage") == "role-denied");
        deniedEntry.Should().NotBeNull(
            "the brief mandates a Warning-level audit log entry on role denial");
        deniedEntry!.Level.Should().Be(LogLevel.Warning);
        deniedEntry.GetValue<string>("RequiredRole").Should().Be(expectedRole);
        deniedEntry.GetValue<string>("Command").Should().Be(commandName);
    }

    private static void AssertRouted(
        RoleEnforcementHarness harness,
        PipelineResult result,
        string expectedResponse)
    {
        result.Handled.Should().BeTrue();
        result.Succeeded.Should().BeTrue();
        result.ResponseText.Should().Be(expectedResponse);
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // Reusable, focused pipeline harness for Stage 5.2 tests. Mirrors
    // the (private) Harness in TelegramUpdatePipelineTests but exposes
    // only what these tests need so the surface stays small.
    // ============================================================

    private sealed class RoleEnforcementHarness
    {
        public Mock<IDeduplicationService> DedupStub { get; }
        public Mock<IUserAuthorizationService> AuthzStub { get; }
        public Mock<ICommandParser> ParserStub { get; }
        public Mock<ICommandRouter> RouterStub { get; }
        public Mock<ICallbackHandler> CallbackStub { get; }
        public Mock<IPendingQuestionStore> PendingStub { get; }
        public InMemoryPendingDisambiguationStore DisambiguationStore { get; }
        public CapturingLogger LogCapture { get; }
        public TelegramUpdatePipeline Pipeline { get; }

        public RoleEnforcementHarness()
        {
            DedupStub = new Mock<IDeduplicationService>(MockBehavior.Strict);
            DedupStub.Setup(d => d.TryReserveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            DedupStub.Setup(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            DedupStub.Setup(d => d.ReleaseReservationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            DedupStub.Setup(d => d.IsProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            AuthzStub = new Mock<IUserAuthorizationService>();
            ParserStub = new Mock<ICommandParser>();
            RouterStub = new Mock<ICommandRouter>();
            CallbackStub = new Mock<ICallbackHandler>();
            PendingStub = new Mock<IPendingQuestionStore>();

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2024, 06, 15, 12, 00, 00, TimeSpan.Zero));
            DisambiguationStore = new InMemoryPendingDisambiguationStore(timeProvider);

            LogCapture = new CapturingLogger();

            Pipeline = new TelegramUpdatePipeline(
                DedupStub.Object,
                AuthzStub.Object,
                ParserStub.Object,
                RouterStub.Object,
                CallbackStub.Object,
                PendingStub.Object,
                DisambiguationStore,
                timeProvider,
                LogCapture);
        }

        public void AuthorizeWith(params OperatorBinding[] bindings)
        {
            var result = new AuthorizationResult
            {
                IsAuthorized = bindings.Length > 0,
                Bindings = bindings,
            };
            // Stage 5.2 (iter-3) -- pipeline calls the 5-arg
            // AuthorizeAsync unconditionally; mock that overload so
            // every pipeline test gets the stubbed bindings.
            AuthzStub.Setup(s => s.AuthorizeAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
            AuthzStub.Setup(s => s.AuthorizeAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
            AuthzStub.Setup(s => s.OnboardAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        }

        public void SetupCommand(string commandName, string rawCommand)
        {
            ParserStub.Setup(p => p.Parse(rawCommand))
                .Returns(new ParsedCommand
                {
                    CommandName = commandName,
                    RawText = rawCommand,
                    IsValid = true,
                });
        }

        public void SetupRouterSuccess(string responseText)
        {
            RouterStub.Setup(r => r.RouteAsync(
                    It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CommandResult
                {
                    Success = true,
                    ResponseText = responseText,
                    CorrelationId = "trace-router",
                });
        }

        public OperatorBinding MakeBinding(
            long telegramUserId = 100,
            long telegramChatId = 200,
            string workspaceId = "w-1",
            string[]? roles = null) =>
            new()
            {
                Id = Guid.NewGuid(),
                TelegramUserId = telegramUserId,
                TelegramChatId = telegramChatId,
                ChatType = ChatType.Private,
                OperatorAlias = "@op",
                TenantId = "t-1",
                WorkspaceId = workspaceId,
                Roles = roles ?? Array.Empty<string>(),
                RegisteredAt = DateTimeOffset.UtcNow,
            };

        public MessengerEvent MakeCommand(string rawCommand, string? eventId = null) =>
            new()
            {
                EventId = eventId ?? Guid.NewGuid().ToString(),
                EventType = EventType.Command,
                RawCommand = rawCommand,
                UserId = "100",
                ChatId = "200",
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = "trace-" + (eventId ?? "cmd"),
            };
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CapturingLogger : ILogger<TelegramUpdatePipeline>
    {
        private readonly object _gate = new();
        private readonly List<CapturedEntry> _entries = new();

        public IReadOnlyList<CapturedEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new CapturedEntry(logLevel, formatter(state, exception));
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var kvp in structured)
                {
                    entry.Properties[kvp.Key] = kvp.Value;
                }
            }
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    public sealed class CapturedEntry
    {
        public CapturedEntry(LogLevel level, string message)
        {
            Level = level;
            Message = message;
        }

        public LogLevel Level { get; }
        public string Message { get; }
        public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);

        public T? GetValue<T>(string key)
            where T : class
        {
            if (Properties.TryGetValue(key, out var value))
            {
                if (value is T typed) { return typed; }
                return value?.ToString() as T;
            }
            return null;
        }
    }

    // ============================================================
    // Minimal mutable IOptionsMonitor<T> that fires change listeners
    // -- matches the production OptionsMonitor<T>'s observable contract
    // closely enough that the SUT's CurrentValue read on every call
    // picks up the new value without requiring a full IConfiguration
    // reload stack in the test fixture.
    // ============================================================

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly ConcurrentDictionary<Guid, Action<T, string?>> _listeners = new();
        private T _current;

        public MutableOptionsMonitor(T initial)
        {
            _current = initial;
        }

        public T CurrentValue => _current;

        public T Get(string? name) => _current;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            var key = Guid.NewGuid();
            _listeners[key] = listener;
            return new Unsubscriber(_listeners, key);
        }

        public void TriggerChange(T newValue)
        {
            _current = newValue;
            foreach (var listener in _listeners.Values)
            {
                listener(newValue, null);
            }
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly ConcurrentDictionary<Guid, Action<T, string?>> _listeners;
            private readonly Guid _key;

            public Unsubscriber(ConcurrentDictionary<Guid, Action<T, string?>> listeners, Guid key)
            {
                _listeners = listeners;
                _key = key;
            }

            public void Dispose() => _listeners.TryRemove(_key, out _);
        }
    }
}
