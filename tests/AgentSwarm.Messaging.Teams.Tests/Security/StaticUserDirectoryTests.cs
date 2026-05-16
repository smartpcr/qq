using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Security;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class StaticUserDirectoryTests
{
    [Fact]
    public async Task Add_AndLookup_RoundTripsByAadObjectId()
    {
        var dir = new StaticUserDirectory();
        var identity = new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: "aad-obj-dave",
            DisplayName: "Dave Contoso",
            Role: "Operator");

        dir.Add(identity);

        var hit = await dir.LookupAsync("aad-obj-dave", CancellationToken.None);
        Assert.Same(identity, hit);
    }

    [Fact]
    public async Task LookupAsync_MissingAadObjectId_ReturnsNull()
    {
        var dir = new StaticUserDirectory();

        var hit = await dir.LookupAsync("not-here", CancellationToken.None);

        Assert.Null(hit);
    }

    [Fact]
    public async Task LookupAsync_EmptyAadObjectId_ReturnsNull()
    {
        var dir = new StaticUserDirectory();

        var hit = await dir.LookupAsync(string.Empty, CancellationToken.None);

        Assert.Null(hit);
    }

    [Fact]
    public void Add_ReplacesExistingEntryForSameAadObjectId()
    {
        var dir = new StaticUserDirectory();
        var first = new UserIdentity("internal-1", "aad-1", "First", "Viewer");
        var second = new UserIdentity("internal-1", "aad-1", "First (renamed)", "Operator");

        dir.Add(first);
        dir.Add(second);

        Assert.Single(dir.Entries);
        Assert.Equal("Operator", dir.Entries.Single().Role);
    }

    [Fact]
    public void Add_NullIdentity_Throws()
    {
        var dir = new StaticUserDirectory();
        Assert.Throws<ArgumentNullException>(() => dir.Add(null!));
    }

    [Fact]
    public void Add_IdentityWithBlankAadObjectId_Throws()
    {
        var dir = new StaticUserDirectory();
        Assert.Throws<ArgumentException>(
            () => dir.Add(new UserIdentity("internal-1", "", "Anonymous", "Viewer")));
    }

    [Fact]
    public async Task LookupAsync_CancellationRequested_Throws()
    {
        var dir = new StaticUserDirectory();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => dir.LookupAsync("aad-anything", cts.Token));
    }

    [Fact]
    public async Task Add_IsThreadSafe()
    {
        var dir = new StaticUserDirectory();
        var tasks = new List<Task>();
        for (var i = 0; i < 32; i++)
        {
            var aadObjectId = $"aad-obj-{i}";
            tasks.Add(Task.Run(() => dir.Add(new UserIdentity($"internal-{i}", aadObjectId, $"User {i}", "Viewer"))));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(32, dir.Entries.Count);
        for (var i = 0; i < 32; i++)
        {
            Assert.NotNull(await dir.LookupAsync($"aad-obj-{i}", CancellationToken.None));
        }
    }
}
