using AgentSwarm.Messaging.Teams.Diagnostics;
using AgentSwarm.Messaging.Teams.Tests.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Unit tests for <see cref="ConversationReferenceStoreHealthCheck"/>. Drives the
/// §6.3 test scenario directly:
/// <i>"Given the database is unreachable, When /health is called, Then it returns
/// Degraded with detail ConversationReferenceStore: Unhealthy"</i>.
/// </summary>
public sealed class ConversationReferenceStoreHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_StoreThrows_ReturnsDegradedWithCanonicalPrefix()
    {
        var store = new SecurityTestDoubles.StubConversationReferenceStore
        {
            CountActiveAsyncThrow = new InvalidOperationException("db-down"),
        };
        var check = new ConversationReferenceStoreHealthCheck(store, NullLogger<ConversationReferenceStoreHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotNull(result.Description);
        Assert.StartsWith(
            ConversationReferenceStoreHealthCheck.UnhealthyDescriptionPrefix,
            result.Description!,
            StringComparison.Ordinal);
        Assert.Contains("ConversationReferenceStore: Unhealthy", result.Description);
        Assert.Contains("db-down", result.Description);
        Assert.NotNull(result.Exception);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    [Fact]
    public async Task CheckHealthAsync_StoreReachable_ReturnsHealthyWithReferenceCount()
    {
        var store = new SecurityTestDoubles.StubConversationReferenceStore
        {
            ActiveSnapshot = new[]
            {
                NewReference("ref-1"),
                NewReference("ref-2"),
                NewReference("ref-3"),
            },
        };
        var check = new ConversationReferenceStoreHealthCheck(store, NullLogger<ConversationReferenceStoreHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("ConversationReferenceStore: Healthy", result.Description);
        Assert.Equal(3L, result.Data["referenceCount"]);
        Assert.Contains("3 active reference", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_EmptyStore_ReportsZeroReferences()
    {
        var store = new SecurityTestDoubles.StubConversationReferenceStore();
        var check = new ConversationReferenceStoreHealthCheck(store, NullLogger<ConversationReferenceStoreHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0L, result.Data["referenceCount"]);
    }

    [Fact]
    public async Task CheckHealthAsync_StoreReturnsUnsupportedSentinel_ReportsHealthyWithUnsupportedLabel()
    {
        var store = new SecurityTestDoubles.StubConversationReferenceStore
        {
            CountActiveResult = -1L,
        };
        var check = new ConversationReferenceStoreHealthCheck(store, NullLogger<ConversationReferenceStoreHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("unsupported", result.Data["referenceCount"]);
        Assert.Contains("aggregate count not supported", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_StoreReturnsRealCountDirectly_UsesCountResultNotSnapshot()
    {
        var store = new SecurityTestDoubles.StubConversationReferenceStore
        {
            CountActiveResult = 4_200L,
        };
        var check = new ConversationReferenceStoreHealthCheck(store, NullLogger<ConversationReferenceStoreHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(4_200L, result.Data["referenceCount"]);
        Assert.Contains("4200 active reference", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_HostCancellation_PropagatesOperationCanceled()
    {
        var store = new SecurityTestDoubles.StubConversationReferenceStore();
        var check = new ConversationReferenceStoreHealthCheck(store, NullLogger<ConversationReferenceStoreHealthCheck>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConversationReferenceStoreHealthCheck(
            referenceStore: null!,
            logger: NullLogger<ConversationReferenceStoreHealthCheck>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConversationReferenceStoreHealthCheck(
            referenceStore: new SecurityTestDoubles.StubConversationReferenceStore(),
            logger: null!));
    }

    private static TeamsConversationReference NewReference(string aadObjectId) => new()
    {
        Id = $"id-{aadObjectId}",
        TenantId = "tenant-1",
        AadObjectId = aadObjectId,
        ChannelId = "msteams",
        ServiceUrl = "https://smba.trafficmanager.net/amer/",
        ConversationId = $"conv-{aadObjectId}",
        BotId = "bot-1",
        ReferenceJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
