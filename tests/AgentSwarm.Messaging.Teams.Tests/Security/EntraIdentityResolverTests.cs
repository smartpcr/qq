using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class EntraIdentityResolverTests
{
    [Fact]
    public async Task ResolveAsync_MappedAadObjectId_ReturnsIdentity()
    {
        var directory = new StubUserDirectory().Add(
            new UserIdentity(
                InternalUserId: "internal-dave",
                AadObjectId: "aad-obj-dave",
                DisplayName: "Dave Contoso",
                Role: "Operator"));
        var resolver = new EntraIdentityResolver(directory, NullLogger<EntraIdentityResolver>.Instance);

        var identity = await resolver.ResolveAsync("aad-obj-dave", CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("internal-dave", identity!.InternalUserId);
        Assert.Equal("aad-obj-dave", identity.AadObjectId);
    }

    [Fact]
    public async Task ResolveAsync_UnmappedAadObjectId_ReturnsNull()
    {
        var directory = new StubUserDirectory();
        var resolver = new EntraIdentityResolver(directory, NullLogger<EntraIdentityResolver>.Instance);

        var identity = await resolver.ResolveAsync("aad-obj-eve-external", CancellationToken.None);

        Assert.Null(identity);
        Assert.Equal(new[] { "aad-obj-eve-external" }, directory.Calls);
    }

    [Fact]
    public async Task ResolveAsync_EmptyAadObjectId_ReturnsNullWithoutTouchingDirectory()
    {
        var directory = new StubUserDirectory();
        var resolver = new EntraIdentityResolver(directory, NullLogger<EntraIdentityResolver>.Instance);

        var identity = await resolver.ResolveAsync(string.Empty, CancellationToken.None);

        Assert.Null(identity);
        Assert.Empty(directory.Calls);
    }

    [Fact]
    public async Task ResolveAsync_CancellationRequested_Throws()
    {
        var directory = new StubUserDirectory();
        var resolver = new EntraIdentityResolver(directory, NullLogger<EntraIdentityResolver>.Instance);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => resolver.ResolveAsync("aad-obj-anyone", cts.Token));
    }

    [Fact]
    public void Constructor_NullDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EntraIdentityResolver(null!, NullLogger<EntraIdentityResolver>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EntraIdentityResolver(new StubUserDirectory(), null!));
    }
}
