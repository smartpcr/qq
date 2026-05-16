using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Swarm;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.7 — locks the <see cref="StubOperatorRegistry"/> contract:
/// projection from <see cref="TelegramOptions.DevOperators"/>, the
/// deterministic id derivation, the active-tenant enumeration, and the
/// read-only <see cref="StubOperatorRegistry.RegisterAsync"/> behaviour.
/// </summary>
public class StubOperatorRegistryTests
{
    [Fact]
    public async Task GetActiveTenants_ReturnsDistinctTenantIds()
    {
        var registry = CreateRegistry(
            Binding(userId: 1, chatId: 100, tenantId: "t-a", workspaceId: "w-1"),
            Binding(userId: 2, chatId: 200, tenantId: "t-a", workspaceId: "w-2"),
            Binding(userId: 3, chatId: 300, tenantId: "t-b", workspaceId: "w-3"));

        var tenants = await registry.GetActiveTenantsAsync(default);

        tenants.Should().BeEquivalentTo(new[] { "t-a", "t-b" });
    }

    [Fact]
    public async Task GetByTenant_ReturnsAllBindingsInTenant()
    {
        var registry = CreateRegistry(
            Binding(userId: 1, chatId: 100, tenantId: "t-a", workspaceId: "w-1"),
            Binding(userId: 2, chatId: 200, tenantId: "t-a", workspaceId: "w-2"),
            Binding(userId: 3, chatId: 300, tenantId: "t-b", workspaceId: "w-3"));

        var bindings = await registry.GetByTenantAsync("t-a", default);

        bindings.Should().HaveCount(2);
        bindings.Select(b => b.TelegramChatId).Should().BeEquivalentTo(new long[] { 100, 200 });
    }

    [Fact]
    public async Task GetByWorkspace_ReturnsBindingsForWorkspace()
    {
        var registry = CreateRegistry(
            Binding(userId: 1, chatId: 100, tenantId: "t-a", workspaceId: "w-1"),
            Binding(userId: 2, chatId: 200, tenantId: "t-a", workspaceId: "w-2"));

        var bindings = await registry.GetByWorkspaceAsync("w-1", default);

        bindings.Should().ContainSingle()
            .Which.TelegramChatId.Should().Be(100);
    }

    [Fact]
    public async Task IsAuthorized_TrueForMatchingUserAndChat()
    {
        var registry = CreateRegistry(Binding(userId: 42, chatId: 555, tenantId: "t-a", workspaceId: "w-1"));

        (await registry.IsAuthorizedAsync(42, 555, default)).Should().BeTrue();
        (await registry.IsAuthorizedAsync(42, 999, default)).Should().BeFalse();
    }

    [Fact]
    public async Task GetByAlias_TenantScoped_DoesNotLeakAcrossTenants()
    {
        var registry = CreateRegistry(
            Binding(userId: 1, chatId: 100, tenantId: "t-a", workspaceId: "w-1", alias: "@dup"),
            Binding(userId: 2, chatId: 200, tenantId: "t-b", workspaceId: "w-2", alias: "@dup"));

        var aMatch = await registry.GetByAliasAsync("@dup", "t-a", default);
        var bMatch = await registry.GetByAliasAsync("@dup", "t-b", default);

        aMatch.Should().NotBeNull();
        aMatch!.TenantId.Should().Be("t-a");
        bMatch.Should().NotBeNull();
        bMatch!.TenantId.Should().Be("t-b");
    }

    [Fact]
    public async Task RegisterAsync_AlwaysThrows()
    {
        var registry = CreateRegistry();
        var registration = new OperatorRegistration
        {
            TelegramUserId = 1,
            TelegramChatId = 2,
            ChatType = ChatType.Private,
            OperatorAlias = "@op",
            TenantId = "t-a",
            WorkspaceId = "w-1",
            Roles = Array.Empty<string>(),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RegisterAsync(registration, default));
        ex.Message.Should().Contain("StubOperatorRegistry");
    }

    [Fact]
    public async Task BindingId_IsDeterministicAcrossCalls()
    {
        var registry = CreateRegistry(
            Binding(userId: 7, chatId: 800, tenantId: "t-a", workspaceId: "w-1"));

        var first = (await registry.GetByTenantAsync("t-a", default)).Single().Id;
        var second = (await registry.GetByTenantAsync("t-a", default)).Single().Id;
        var direct = StubOperatorRegistry.DeriveBindingId(7, 800, "t-a", "w-1");

        first.Should().Be(second);
        first.Should().Be(direct);
    }

    [Fact]
    public async Task DevOperators_WithBlankTenant_AreSkippedSilently()
    {
        var registry = CreateRegistry(
            new TelegramOperatorBindingOptions
            {
                TelegramUserId = 1,
                TelegramChatId = 100,
                TenantId = "",     // blank → skip
                WorkspaceId = "w-1",
                OperatorAlias = "@bad",
            },
            Binding(userId: 2, chatId: 200, tenantId: "t-a", workspaceId: "w-1"));

        var bindings = await registry.GetByTenantAsync("t-a", default);
        bindings.Should().ContainSingle()
            .Which.TelegramChatId.Should().Be(200);
    }

    private static StubOperatorRegistry CreateRegistry(params TelegramOperatorBindingOptions[] bindings)
    {
        var options = new TelegramOptions();
        options.DevOperators.AddRange(bindings);
        var monitor = new StaticOptionsMonitor<TelegramOptions>(options);
        return new StubOperatorRegistry(monitor);
    }

    private static TelegramOperatorBindingOptions Binding(
        long userId,
        long chatId,
        string tenantId,
        string workspaceId,
        string alias = "@op") => new()
        {
            TelegramUserId = userId,
            TelegramChatId = chatId,
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            OperatorAlias = alias,
        };

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
