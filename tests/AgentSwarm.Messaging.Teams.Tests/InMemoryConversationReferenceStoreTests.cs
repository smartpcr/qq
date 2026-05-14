using AgentSwarm.Messaging.Teams;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class InMemoryConversationReferenceStoreTests
{
    private const string Tenant = "tenant-a";

    [Fact]
    public async Task SaveOrUpdate_ThenGetByAad_RoundTrips()
    {
        var store = new InMemoryConversationReferenceStore();
        var reference = MakePersonal(aadObjectId: "aad-1", internalUserId: "u-1");

        await store.SaveOrUpdateAsync(reference, default);
        var fetched = await store.GetByAadObjectIdAsync(Tenant, "aad-1", default);

        Assert.NotNull(fetched);
        Assert.Equal(reference.Id, fetched!.Id);
    }

    [Fact]
    public async Task GetByInternalUserId_ReturnsActiveOnly()
    {
        var store = new InMemoryConversationReferenceStore();
        var reference = MakePersonal(aadObjectId: "aad-2", internalUserId: "u-2") with { IsActive = false };

        await store.SaveOrUpdateAsync(reference, default);
        var fetched = await store.GetByInternalUserIdAsync(Tenant, "u-2", default);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task IsActiveByInternalUserId_DistinguishesFromMissing()
    {
        var store = new InMemoryConversationReferenceStore();
        var reference = MakePersonal(aadObjectId: "aad-3", internalUserId: "u-3");

        await store.SaveOrUpdateAsync(reference, default);

        var presentActive = await store.IsActiveByInternalUserIdAsync(Tenant, "u-3", default);
        var missing = await store.IsActiveByInternalUserIdAsync(Tenant, "u-missing", default);

        Assert.True(presentActive);
        Assert.False(missing);
    }

    [Fact]
    public async Task MarkInactive_FlipsIsActive_AndIsActiveAsyncReturnsFalse()
    {
        var store = new InMemoryConversationReferenceStore();
        var reference = MakePersonal(aadObjectId: "aad-4", internalUserId: "u-4");

        await store.SaveOrUpdateAsync(reference, default);
        await store.MarkInactiveAsync(Tenant, "aad-4", default);

        Assert.False(await store.IsActiveAsync(Tenant, "aad-4", default));
        var fromAad = await store.GetByAadObjectIdAsync(Tenant, "aad-4", default);
        Assert.Null(fromAad);
    }

    [Fact]
    public async Task ChannelScope_IsActiveByChannelAndMarkInactiveByChannel()
    {
        var store = new InMemoryConversationReferenceStore();
        var channelRef = MakeChannel(channelId: "ch-1");

        await store.SaveOrUpdateAsync(channelRef, default);
        Assert.True(await store.IsActiveByChannelAsync(Tenant, "ch-1", default));

        await store.MarkInactiveByChannelAsync(Tenant, "ch-1", default);
        Assert.False(await store.IsActiveByChannelAsync(Tenant, "ch-1", default));
    }

    [Fact]
    public async Task SaveOrUpdate_UpsertsByNaturalKey()
    {
        var store = new InMemoryConversationReferenceStore();
        var first = MakePersonal(aadObjectId: "aad-5", internalUserId: "u-5") with { Id = "id-a", ConversationId = "conv-a" };
        var resaved = MakePersonal(aadObjectId: "aad-5", internalUserId: "u-5") with { Id = "id-b", ConversationId = "conv-b" };

        await store.SaveOrUpdateAsync(first, default);
        await store.SaveOrUpdateAsync(resaved, default);

        var byAad = await store.GetByAadObjectIdAsync(Tenant, "aad-5", default);
        Assert.NotNull(byAad);
        Assert.Equal("id-a", byAad!.Id);
        Assert.Equal("conv-b", byAad.ConversationId);
    }

    [Fact]
    public async Task GetAllActive_FiltersByTenantAndActiveFlag()
    {
        var store = new InMemoryConversationReferenceStore();
        await store.SaveOrUpdateAsync(MakePersonal("aad-6", "u-6"), default);
        await store.SaveOrUpdateAsync(MakePersonal("aad-7", "u-7") with { IsActive = false }, default);
        await store.SaveOrUpdateAsync(MakePersonal("aad-8", "u-8") with { TenantId = "tenant-b" }, default);

        var actives = await store.GetAllActiveAsync(Tenant, default);

        Assert.Single(actives);
        Assert.Equal("aad-6", actives[0].AadObjectId);
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        var store = new InMemoryConversationReferenceStore();
        await store.SaveOrUpdateAsync(MakePersonal("aad-9", "u-9"), default);

        await store.DeleteAsync(Tenant, "aad-9", default);

        Assert.Empty(await store.GetAllActiveAsync(Tenant, default));
    }

    private static TeamsConversationReference MakePersonal(string aadObjectId, string internalUserId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        TenantId = Tenant,
        AadObjectId = aadObjectId,
        InternalUserId = internalUserId,
        ServiceUrl = "https://smba.example/",
        ConversationId = "conv-" + aadObjectId,
        BotId = "bot-id",
        ReferenceJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static TeamsConversationReference MakeChannel(string channelId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        TenantId = Tenant,
        ChannelId = channelId,
        ServiceUrl = "https://smba.example/",
        ConversationId = "conv-" + channelId,
        BotId = "bot-id",
        ReferenceJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
