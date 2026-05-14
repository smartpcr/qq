using AgentSwarm.Messaging.Teams.Storage;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class InMemoryConversationReferenceStoreTests
{
    private static TeamsConversationReference MakeReference(
        string tenantId = "tenant",
        string? aadObjectId = "aad",
        string? internalUserId = "internal",
        string? channelId = null,
        bool active = true)
    {
        var now = DateTimeOffset.UtcNow;
        return new TeamsConversationReference
        {
            TenantId = tenantId,
            AadObjectId = aadObjectId,
            InternalUserId = internalUserId,
            ChannelId = channelId,
            SerializedReference = "{}",
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = active,
        };
    }

    [Fact]
    public async Task SaveOrUpdate_Then_GetByAadObjectId_Returns_Reference()
    {
        var store = new InMemoryConversationReferenceStore();
        var reference = MakeReference();
        await store.SaveOrUpdateAsync(reference, default);

        var got = await store.GetByAadObjectIdAsync("tenant", "aad", default);
        Assert.NotNull(got);
        Assert.Equal("aad", got!.AadObjectId);
    }

    [Fact]
    public async Task GetByInternalUserId_Returns_Reference()
    {
        var store = new InMemoryConversationReferenceStore();
        await store.SaveOrUpdateAsync(MakeReference(), default);

        var got = await store.GetByInternalUserIdAsync("tenant", "internal", default);
        Assert.NotNull(got);
    }

    [Fact]
    public async Task MarkInactive_Hides_Reference_From_Lookups_But_Probe_Returns_False()
    {
        var store = new InMemoryConversationReferenceStore();
        await store.SaveOrUpdateAsync(MakeReference(), default);

        Assert.True(await store.IsActiveAsync("tenant", "aad", default));
        await store.MarkInactiveAsync("tenant", "aad", default);

        Assert.Null(await store.GetByAadObjectIdAsync("tenant", "aad", default));
        Assert.False(await store.IsActiveAsync("tenant", "aad", default));
        Assert.False(await store.IsActiveByInternalUserIdAsync("tenant", "internal", default));
    }

    [Fact]
    public async Task ChannelScope_MarkInactive_And_IsActiveByChannel()
    {
        var store = new InMemoryConversationReferenceStore();
        await store.SaveOrUpdateAsync(MakeReference(aadObjectId: null, internalUserId: null, channelId: "ch1"), default);

        Assert.True(await store.IsActiveByChannelAsync("tenant", "ch1", default));
        await store.MarkInactiveByChannelAsync("tenant", "ch1", default);
        Assert.False(await store.IsActiveByChannelAsync("tenant", "ch1", default));
    }

    [Fact]
    public async Task Delete_Removes_Reference_Completely()
    {
        var store = new InMemoryConversationReferenceStore();
        await store.SaveOrUpdateAsync(MakeReference(), default);
        await store.DeleteAsync("tenant", "aad", default);

        Assert.False(await store.IsActiveAsync("tenant", "aad", default));
        Assert.Null(await store.GetByAadObjectIdAsync("tenant", "aad", default));
    }
}
