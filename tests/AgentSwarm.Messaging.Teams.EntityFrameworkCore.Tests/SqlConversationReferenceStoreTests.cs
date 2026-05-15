using AgentSwarm.Messaging.Teams;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Stage 4.1 acceptance tests for <see cref="SqlConversationReferenceStore"/> covering every
/// test scenario enumerated in <c>implementation-plan.md</c> §4.1 plus the dual-key /
/// dual-scope edge cases called out in <c>architecture.md</c> §4.2.
/// </summary>
/// <remarks>
/// The fixture wires an in-memory SQLite database via
/// <see cref="StoreFixture"/> so the EF Core model — including the four filtered indexes
/// declared by <see cref="TeamsConversationReferenceDbContext"/> — is exercised end-to-end.
/// </remarks>
public class SqlConversationReferenceStoreTests
{
    [Fact(DisplayName = "Save and retrieve by AAD object ID round-trips the reference")]
    public async Task SaveAndRetrieve_ByAadObjectId_RoundTrips()
    {
        await using var fixture = new StoreFixture();
        var reference = TeamsConversationReferenceFactory.UserScoped(
            tenantId: "tenant-1",
            aadObjectId: "aad-user-1",
            referenceJson: "{\"original\":\"payload\"}");

        await fixture.Store.SaveOrUpdateAsync(reference, CancellationToken.None);

        var loaded = await fixture.Store.GetByAadObjectIdAsync("tenant-1", "aad-user-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("aad-user-1", loaded.AadObjectId);
        Assert.Equal("tenant-1", loaded.TenantId);
        Assert.Equal("{\"original\":\"payload\"}", loaded.ReferenceJson);
        Assert.Equal(reference.ConversationId, loaded.ConversationId);
        Assert.Equal(reference.ServiceUrl, loaded.ServiceUrl);
        Assert.True(loaded.IsActive);
    }

    [Fact(DisplayName = "Retrieve by internal user ID returns reference with both identity keys")]
    public async Task GetByInternalUserId_ReturnsReferenceWithBothIdentityKeys()
    {
        await using var fixture = new StoreFixture();
        var reference = TeamsConversationReferenceFactory.UserScoped(
            tenantId: "tenant-1",
            aadObjectId: "aad-user-1",
            internalUserId: "internal-1");

        await fixture.Store.SaveOrUpdateAsync(reference, CancellationToken.None);

        var loaded = await fixture.Store.GetByInternalUserIdAsync("tenant-1", "internal-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("aad-user-1", loaded.AadObjectId);
        Assert.Equal("internal-1", loaded.InternalUserId);
    }

    [Fact(DisplayName = "Upsert on duplicate user-scoped key updates row instead of inserting")]
    public async Task SaveOrUpdate_DuplicateUserKey_Upserts()
    {
        await using var fixture = new StoreFixture();
        var first = TeamsConversationReferenceFactory.UserScoped(
            aadObjectId: "aad-user-1",
            serviceUrl: "https://smba.original.url/");
        await fixture.Store.SaveOrUpdateAsync(first, CancellationToken.None);

        var second = TeamsConversationReferenceFactory.UserScoped(
            aadObjectId: "aad-user-1",
            serviceUrl: "https://smba.refreshed.url/");
        await fixture.Store.SaveOrUpdateAsync(second, CancellationToken.None);

        await using var context = fixture.CreateContext();
        var rows = await context.ConversationReferences
            .Where(e => e.AadObjectId == "aad-user-1")
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal("https://smba.refreshed.url/", rows[0].ServiceUrl);
    }

    [Fact(DisplayName = "Multi-tenant queries are scoped to the requested tenant")]
    public async Task Queries_AreTenantScoped()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(tenantId: "tenant-1", aadObjectId: "aad-user-1"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(tenantId: "tenant-2", aadObjectId: "aad-user-2"),
            CancellationToken.None);

        var tenant1 = await fixture.Store.GetAllActiveAsync("tenant-1", CancellationToken.None);
        var tenant2 = await fixture.Store.GetAllActiveAsync("tenant-2", CancellationToken.None);

        Assert.Single(tenant1);
        Assert.Equal("aad-user-1", tenant1[0].AadObjectId);
        Assert.Single(tenant2);
        Assert.Equal("aad-user-2", tenant2[0].AadObjectId);
    }

    [Fact(DisplayName = "Channel reference upsert keys on (ChannelId, TenantId)")]
    public async Task ChannelReference_UpsertOnChannelTenant()
    {
        await using var fixture = new StoreFixture();
        var first = TeamsConversationReferenceFactory.ChannelScoped(
            channelId: "channel-general",
            serviceUrl: "https://smba.original.url/");
        await fixture.Store.SaveOrUpdateAsync(first, CancellationToken.None);

        var second = TeamsConversationReferenceFactory.ChannelScoped(
            channelId: "channel-general",
            serviceUrl: "https://smba.refreshed.url/");
        await fixture.Store.SaveOrUpdateAsync(second, CancellationToken.None);

        await using var context = fixture.CreateContext();
        var rows = await context.ConversationReferences
            .Where(e => e.ChannelId == "channel-general" && e.TenantId == "tenant-1")
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal("https://smba.refreshed.url/", rows[0].ServiceUrl);
        Assert.Null(rows[0].AadObjectId);
    }

    [Fact(DisplayName = "User and channel references coexist as distinct rows")]
    public async Task UserAndChannel_References_Coexist()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-1"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-general"),
            CancellationToken.None);

        var userHit = await fixture.Store.GetByAadObjectIdAsync("tenant-1", "aad-user-1", CancellationToken.None);
        var channelHit = await fixture.Store.GetByChannelIdAsync("tenant-1", "channel-general", CancellationToken.None);

        Assert.NotNull(userHit);
        Assert.NotNull(channelHit);
        Assert.NotEqual(userHit.Id, channelHit.Id);
        Assert.Equal("aad-user-1", userHit.AadObjectId);
        Assert.Null(userHit.ChannelId);
        Assert.Equal("channel-general", channelHit.ChannelId);
        Assert.Null(channelHit.AadObjectId);

        await using var context = fixture.CreateContext();
        var rowCount = await context.ConversationReferences.CountAsync();
        Assert.Equal(2, rowCount);
    }

    [Fact(DisplayName = "MarkInactiveByChannelAsync soft-deletes with audit metadata")]
    public async Task MarkInactiveByChannel_RetainsRowWithAuditFields()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new StoreFixture(time);

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-general"),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(5));
        await fixture.Store.MarkInactiveByChannelAsync("tenant-1", "channel-general", CancellationToken.None);

        await using var context = fixture.CreateContext();
        var entity = await context.ConversationReferences
            .FirstOrDefaultAsync(e => e.ChannelId == "channel-general");

        Assert.NotNull(entity);
        Assert.False(entity.IsActive);
        Assert.Equal(new DateTimeOffset(2026, 5, 14, 12, 5, 0, TimeSpan.Zero), entity.DeactivatedAt);
        Assert.Equal(ConversationReferenceDeactivationReasons.Uninstalled, entity.DeactivationReason);
    }

    [Fact(DisplayName = "DeleteByChannelAsync permanently removes the channel row")]
    public async Task DeleteByChannel_PermanentlyRemovesRow()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-general"),
            CancellationToken.None);
        await fixture.Store.MarkInactiveByChannelAsync("tenant-1", "channel-general", CancellationToken.None);

        await fixture.Store.DeleteByChannelAsync("tenant-1", "channel-general", CancellationToken.None);

        await using var context = fixture.CreateContext();
        var any = await context.ConversationReferences.AnyAsync(e => e.ChannelId == "channel-general");
        Assert.False(any);
    }

    [Fact(DisplayName = "GetAsync returns reference regardless of IsActive status")]
    public async Task GetAsync_ReturnsInactiveReference()
    {
        await using var fixture = new StoreFixture();
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-inactive"),
            CancellationToken.None);
        await fixture.Store.MarkInactiveAsync("tenant-1", "aad-user-inactive", CancellationToken.None);

        var loaded = await fixture.Store.GetAsync("tenant-1", "aad-user-inactive", CancellationToken.None);
        var activeOnly = await fixture.Store.GetByAadObjectIdAsync("tenant-1", "aad-user-inactive", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.False(loaded.IsActive);
        Assert.Null(activeOnly);
    }

    [Fact(DisplayName = "GetAllActiveAsync excludes inactive references")]
    public async Task GetAllActive_ExcludesInactive()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-1"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-2"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-3"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-general"),
            CancellationToken.None);

        await fixture.Store.MarkInactiveAsync("tenant-1", "aad-user-3", CancellationToken.None);

        var active = await fixture.Store.GetAllActiveAsync("tenant-1", CancellationToken.None);

        Assert.Equal(3, active.Count);
        Assert.DoesNotContain(active, r => r.AadObjectId == "aad-user-3");
        Assert.Contains(active, r => r.AadObjectId == "aad-user-1");
        Assert.Contains(active, r => r.AadObjectId == "aad-user-2");
        Assert.Contains(active, r => r.ChannelId == "channel-general");
    }

    [Fact(DisplayName = "DeleteAsync permanently removes a user-scoped reference")]
    public async Task DeleteAsync_PermanentlyRemovesUserReference()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-cleanup"),
            CancellationToken.None);
        await fixture.Store.MarkInactiveAsync("tenant-1", "aad-user-cleanup", CancellationToken.None);

        await fixture.Store.DeleteAsync("tenant-1", "aad-user-cleanup", CancellationToken.None);

        var loaded = await fixture.Store.GetAsync("tenant-1", "aad-user-cleanup", CancellationToken.None);
        Assert.Null(loaded);
    }

    [Fact(DisplayName = "IsActiveByChannelAsync distinguishes active, inactive, and missing references")]
    public async Task IsActiveByChannel_ReportsExpectedTriState()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-general"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-dev"),
            CancellationToken.None);
        await fixture.Store.MarkInactiveByChannelAsync("tenant-1", "channel-general", CancellationToken.None);

        Assert.False(await fixture.Store.IsActiveByChannelAsync("tenant-1", "channel-general", CancellationToken.None));
        Assert.True(await fixture.Store.IsActiveByChannelAsync("tenant-1", "channel-dev", CancellationToken.None));
        Assert.False(await fixture.Store.IsActiveByChannelAsync("tenant-1", "channel-nonexistent", CancellationToken.None));
    }

    [Fact(DisplayName = "IsActiveByInternalUserIdAsync distinguishes active, inactive, and missing references")]
    public async Task IsActiveByInternalUserId_ReportsExpectedTriState()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(
                tenantId: "tenant-1",
                aadObjectId: "aad-user-1",
                internalUserId: "internal-1"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(
                tenantId: "tenant-1",
                aadObjectId: "aad-user-2",
                internalUserId: "internal-2"),
            CancellationToken.None);
        await fixture.Store.MarkInactiveAsync("tenant-1", "aad-user-2", CancellationToken.None);

        Assert.True(await fixture.Store.IsActiveByInternalUserIdAsync("tenant-1", "internal-1", CancellationToken.None));
        Assert.False(await fixture.Store.IsActiveByInternalUserIdAsync("tenant-1", "internal-2", CancellationToken.None));
        Assert.False(await fixture.Store.IsActiveByInternalUserIdAsync("tenant-1", "internal-nonexistent", CancellationToken.None));
    }

    [Fact(DisplayName = "MarkInactiveAsync sets audit metadata for user-scoped reference")]
    public async Task MarkInactive_RecordsAuditMetadata()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 14, 9, 30, 0, TimeSpan.Zero));
        await using var fixture = new StoreFixture(time);

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-1"),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(15));
        await fixture.Store.MarkInactiveAsync("tenant-1", "aad-user-1", CancellationToken.None);

        await using var context = fixture.CreateContext();
        var entity = await context.ConversationReferences
            .FirstOrDefaultAsync(e => e.AadObjectId == "aad-user-1");

        Assert.NotNull(entity);
        Assert.False(entity.IsActive);
        Assert.Equal(new DateTimeOffset(2026, 5, 14, 9, 45, 0, TimeSpan.Zero), entity.DeactivatedAt);
        Assert.Equal(ConversationReferenceDeactivationReasons.Uninstalled, entity.DeactivationReason);
    }

    [Fact(DisplayName = "Re-saving an inactive reference re-activates it (re-install scenario)")]
    public async Task SaveOrUpdate_OnInactiveReference_Reactivates()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-1"),
            CancellationToken.None);
        await fixture.Store.MarkInactiveAsync("tenant-1", "aad-user-1", CancellationToken.None);

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(
                aadObjectId: "aad-user-1",
                serviceUrl: "https://smba.refresh-after-uninstall/"),
            CancellationToken.None);

        await using var context = fixture.CreateContext();
        var entity = await context.ConversationReferences
            .FirstOrDefaultAsync(e => e.AadObjectId == "aad-user-1");

        Assert.NotNull(entity);
        Assert.True(entity.IsActive);
        Assert.Null(entity.DeactivatedAt);
        Assert.Null(entity.DeactivationReason);
        Assert.Equal("https://smba.refresh-after-uninstall/", entity.ServiceUrl);
    }

    [Fact(DisplayName = "SaveOrUpdate preserves a previously-resolved InternalUserId when the inbound payload omits it")]
    public async Task SaveOrUpdate_PreservesPreviouslyResolvedInternalUserId()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(
                aadObjectId: "aad-user-1",
                internalUserId: "internal-1"),
            CancellationToken.None);

        // Subsequent message arrives before identity resolver writes back — InternalUserId
        // is null on the inbound payload but must NOT clobber the previously-resolved value.
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(
                aadObjectId: "aad-user-1",
                internalUserId: null),
            CancellationToken.None);

        var loaded = await fixture.Store.GetByInternalUserIdAsync("tenant-1", "internal-1", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("aad-user-1", loaded.AadObjectId);
    }

    [Fact(DisplayName = "GetActiveChannelsByTeamIdAsync returns every active channel reference for the team")]
    public async Task GetActiveChannelsByTeamId_ReturnsTeamChannels()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-general", teamId: "team-platform"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-dev", teamId: "team-platform"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-other", teamId: "team-other"),
            CancellationToken.None);

        var teamChannels = await fixture.Store.GetActiveChannelsByTeamIdAsync(
            "tenant-1",
            "team-platform",
            CancellationToken.None);

        Assert.Equal(2, teamChannels.Count);
        Assert.All(teamChannels, c => Assert.Equal("team-platform", c.TeamId));
        Assert.Contains(teamChannels, c => c.ChannelId == "channel-general");
        Assert.Contains(teamChannels, c => c.ChannelId == "channel-dev");
    }

    [Fact(DisplayName = "GetByAadObjectIdAsync returns null for inactive references")]
    public async Task GetByAadObjectId_FiltersOutInactiveReferences()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(aadObjectId: "aad-user-1"),
            CancellationToken.None);
        await fixture.Store.MarkInactiveAsync("tenant-1", "aad-user-1", CancellationToken.None);

        var loaded = await fixture.Store.GetByAadObjectIdAsync("tenant-1", "aad-user-1", CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact(DisplayName = "GetByConversationIdAsync resolves stored reference (router contract)")]
    public async Task GetByConversationId_ReturnsStoredReference()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.UserScoped(
                aadObjectId: "aad-user-1",
                conversationId: "a:conversation-router-test"),
            CancellationToken.None);

        var loaded = await fixture.Store.GetByConversationIdAsync("a:conversation-router-test", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("aad-user-1", loaded.AadObjectId);
    }

    [Fact(DisplayName = "SaveOrUpdate rejects references with neither AadObjectId nor ChannelId set")]
    public async Task SaveOrUpdate_RejectsReferenceWithNoNaturalKey()
    {
        await using var fixture = new StoreFixture();

        var invalid = new TeamsConversationReference
        {
            Id = Guid.NewGuid().ToString("D"),
            TenantId = "tenant-1",
            AadObjectId = null,
            ChannelId = null,
            ServiceUrl = "https://smba.host/",
            ConversationId = "conversation-id",
            BotId = "bot-id",
            ReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Store.SaveOrUpdateAsync(invalid, CancellationToken.None));
    }

    [Fact(DisplayName = "MarkInactiveAsync is a no-op for nonexistent references")]
    public async Task MarkInactive_OnMissingRow_NoOp()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.MarkInactiveAsync("tenant-1", "aad-user-missing", CancellationToken.None);

        var loaded = await fixture.Store.GetAsync("tenant-1", "aad-user-missing", CancellationToken.None);
        Assert.Null(loaded);
    }

    [Fact(DisplayName = "SaveOrUpdate rejects references that populate BOTH AadObjectId and ChannelId (mutually exclusive scopes)")]
    public async Task SaveOrUpdate_RejectsReferenceWithBothNaturalKeysSet()
    {
        await using var fixture = new StoreFixture();

        var ambiguous = new TeamsConversationReference
        {
            Id = Guid.NewGuid().ToString("D"),
            TenantId = "tenant-1",
            AadObjectId = "aad-user-1",
            ChannelId = "channel-general",
            ServiceUrl = "https://smba.host/",
            ConversationId = "conversation-id",
            BotId = "bot-id",
            ReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Store.SaveOrUpdateAsync(ambiguous, CancellationToken.None));
        Assert.Contains("mutually exclusive", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Confirm no row was persisted as a side effect of the rejected call.
        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.ConversationReferences.CountAsync());
    }

    [Theory(DisplayName = "SaveOrUpdate rejects references with null/empty TenantId (security: tenant-scoped keys)")]
    [InlineData(null)]
    [InlineData("")]
    public async Task SaveOrUpdate_RejectsReferenceWithEmptyTenantId(string? tenantId)
    {
        await using var fixture = new StoreFixture();

        var noTenant = new TeamsConversationReference
        {
            Id = Guid.NewGuid().ToString("D"),
            TenantId = tenantId!,
            AadObjectId = "aad-user-1",
            ChannelId = null,
            ServiceUrl = "https://smba.host/",
            ConversationId = "conversation-id",
            BotId = "bot-id",
            ReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Store.SaveOrUpdateAsync(noTenant, CancellationToken.None));
        Assert.Contains("TenantId", ex.Message, StringComparison.Ordinal);

        // Confirm no row was persisted as a side effect of the rejected call.
        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.ConversationReferences.CountAsync());
    }

    [Fact(DisplayName = "ConversationReferences.IsActive has database default value of true (model + schema)")]
    public async Task IsActive_HasDatabaseDefaultValueOfTrue()
    {
        await using var fixture = new StoreFixture();

        // 1. EF model annotation surfaces the default — required so future migrations
        //    keep emitting `defaultValue: true` on the bit column.
        await using (var ctx = fixture.CreateContext())
        {
            var entityType = ctx.Model.FindEntityType(typeof(ConversationReferenceEntity));
            Assert.NotNull(entityType);
            var isActive = entityType!.FindProperty(nameof(ConversationReferenceEntity.IsActive));
            Assert.NotNull(isActive);
            Assert.Equal(true, isActive!.GetDefaultValue());
        }

        // 2. The DDL emitted by EnsureCreated honors the default — INSERT that omits
        //    IsActive must yield IsActive=true at the database level (not just the
        //    in-memory entity initializer).
        await using (var ctx = fixture.CreateContext())
        {
            var connection = ctx.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO ConversationReferences " +
                "(Id, TenantId, AadObjectId, InternalUserId, ChannelId, TeamId, ServiceUrl, ConversationId, BotId, ConversationJson, DeactivatedAt, DeactivationReason, CreatedAt, UpdatedAt) " +
                "VALUES ('row-default-test', 'tenant-1', 'aad-default-test', NULL, NULL, NULL, 'https://smba.host/', 'conv-1', 'bot-1', '{}', NULL, NULL, '2024-01-01T00:00:00+00:00', '2024-01-01T00:00:00+00:00')";
            var rows = await command.ExecuteNonQueryAsync();
            Assert.Equal(1, rows);
        }

        await using (var ctx = fixture.CreateContext())
        {
            var entity = await ctx.ConversationReferences.SingleAsync(e => e.Id == "row-default-test");
            Assert.True(entity.IsActive);
        }
    }

    [Theory(DisplayName = "SaveOrUpdate rejects references with null/empty ServiceUrl (Bot Framework routing requirement)")]
    [InlineData(null)]
    [InlineData("")]
    public async Task SaveOrUpdate_RejectsReferenceWithEmptyServiceUrl(string? serviceUrl)
    {
        await using var fixture = new StoreFixture();
        var reference = TeamsConversationReferenceFactory.UserScoped(serviceUrl: serviceUrl!);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Store.SaveOrUpdateAsync(reference, CancellationToken.None));
        Assert.Contains("ServiceUrl", ex.Message, StringComparison.Ordinal);

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.ConversationReferences.CountAsync());
    }

    [Theory(DisplayName = "SaveOrUpdate rejects references with null/empty ConversationId (proactive-routing key)")]
    [InlineData(null)]
    [InlineData("")]
    public async Task SaveOrUpdate_RejectsReferenceWithEmptyConversationId(string? conversationId)
    {
        await using var fixture = new StoreFixture();
        var reference = TeamsConversationReferenceFactory.UserScoped(conversationId: conversationId!);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Store.SaveOrUpdateAsync(reference, CancellationToken.None));
        Assert.Contains("ConversationId", ex.Message, StringComparison.Ordinal);

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.ConversationReferences.CountAsync());
    }

    [Theory(DisplayName = "SaveOrUpdate rejects references with null/empty BotId (TurnContext reconstitution)")]
    [InlineData(null)]
    [InlineData("")]
    public async Task SaveOrUpdate_RejectsReferenceWithEmptyBotId(string? botId)
    {
        await using var fixture = new StoreFixture();
        var reference = TeamsConversationReferenceFactory.UserScoped(botId: botId!);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Store.SaveOrUpdateAsync(reference, CancellationToken.None));
        Assert.Contains("BotId", ex.Message, StringComparison.Ordinal);

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.ConversationReferences.CountAsync());
    }

    [Fact(DisplayName = "MarkInactiveByChannelAsync is a no-op for nonexistent channel")]
    public async Task MarkInactiveByChannel_OnMissingRow_NoOp()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.MarkInactiveByChannelAsync("tenant-1", "channel-missing", CancellationToken.None);

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.ConversationReferences.CountAsync());
    }

    [Fact(DisplayName = "DeleteAsync is a no-op for nonexistent user-scoped reference")]
    public async Task Delete_OnMissingRow_NoOp()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.DeleteAsync("tenant-1", "aad-user-missing", CancellationToken.None);

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.ConversationReferences.CountAsync());
    }

    [Fact(DisplayName = "DeleteByChannelAsync is a no-op for nonexistent channel-scoped reference")]
    public async Task DeleteByChannel_OnMissingRow_NoOp()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.DeleteByChannelAsync("tenant-1", "channel-missing", CancellationToken.None);

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.ConversationReferences.CountAsync());
    }

    [Fact(DisplayName = "GetActiveChannelsByTeamIdAsync filters out inactive channels")]
    public async Task GetActiveChannelsByTeamId_FiltersInactiveChannels()
    {
        await using var fixture = new StoreFixture();

        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-active", teamId: "team-platform"),
            CancellationToken.None);
        await fixture.Store.SaveOrUpdateAsync(
            TeamsConversationReferenceFactory.ChannelScoped(channelId: "channel-inactive", teamId: "team-platform"),
            CancellationToken.None);

        // Soft-delete one of the channels in the team.
        await fixture.Store.MarkInactiveByChannelAsync("tenant-1", "channel-inactive", CancellationToken.None);

        var teamChannels = await fixture.Store.GetActiveChannelsByTeamIdAsync(
            "tenant-1",
            "team-platform",
            CancellationToken.None);

        Assert.Single(teamChannels);
        Assert.Equal("channel-active", teamChannels[0].ChannelId);
        Assert.DoesNotContain(teamChannels, c => c.ChannelId == "channel-inactive");
    }
}
