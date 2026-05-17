using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class StaticUserRoleProviderTests
{
    [Fact]
    public async Task GetRoleAsync_ReturnsAssignedRole()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.AssignRole("tenant-1", "aad-obj-alice", RbacOptions.OperatorRole);
        var provider = new StaticUserRoleProvider(WrapInMonitor(options));

        var role = await provider.GetRoleAsync("tenant-1", "aad-obj-alice", CancellationToken.None);

        Assert.Equal(RbacOptions.OperatorRole, role);
    }

    [Fact]
    public async Task GetRoleAsync_FallsBackToDefaultRoleWhenUnassigned()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.DefaultRole = RbacOptions.ViewerRole;
        var provider = new StaticUserRoleProvider(WrapInMonitor(options));

        var role = await provider.GetRoleAsync("tenant-1", "aad-obj-eve", CancellationToken.None);

        Assert.Equal(RbacOptions.ViewerRole, role);
    }

    [Fact]
    public async Task GetRoleAsync_ReturnsNullWhenNoAssignmentAndNoDefault()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var provider = new StaticUserRoleProvider(WrapInMonitor(options));

        var role = await provider.GetRoleAsync("tenant-1", "aad-obj-eve", CancellationToken.None);

        Assert.Null(role);
    }

    [Fact]
    public async Task GetRoleAsync_CancellationRequested_Throws()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var provider = new StaticUserRoleProvider(WrapInMonitor(options));
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.GetRoleAsync("tenant-1", "aad-obj-eve", cts.Token));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StaticUserRoleProvider(null!));
    }

    /// <summary>
    /// Minimal <see cref="IOptionsMonitor{TOptions}"/> backed by a single value snapshot —
    /// the Stage 5.1 unit tests do not exercise hot-reload, so a static monitor is enough.
    /// </summary>
    internal static IOptionsMonitor<T> WrapInMonitor<T>(T value) where T : class, new()
        => new StaticOptionsMonitor<T>(value);

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
