using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;
using static AgentSwarm.Messaging.Teams.Tests.Security.StaticUserRoleProviderTests;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class TeamsAppPolicyHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_AllProbesPass_ReturnsHealthy()
    {
        var check = BuildCheck(out _, out _);

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = NewRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True((bool)result.Data["microsoftAppIdConfigured"]);
        Assert.Equal("healthy", result.Data["botFrameworkAuthentication"]);
        Assert.Equal("healthy", result.Data["conversationReferenceStore"]);
    }

    [Fact]
    public async Task CheckHealthAsync_MissingMicrosoftAppId_ReturnsDegraded_DoesNotCallAuth()
    {
        var messaging = new TeamsMessagingOptions { MicrosoftAppId = string.Empty };
        var auth = new FakeBotFrameworkAuthentication();
        var store = new StubConversationReferenceStore();
        var check = new TeamsAppPolicyHealthCheck(
            WrapInMonitor(messaging),
            WrapInMonitor(new TeamsAppPolicyOptions()),
            auth,
            store,
            NullLogger<TeamsAppPolicyHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = NewRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("MicrosoftAppId", result.Description);
        Assert.Empty(auth.CreateConnectorFactoryCalls);
    }

    [Fact]
    public async Task CheckHealthAsync_BotAuthenticationThrowsDuringCreateAsync_ReturnsDegraded()
    {
        var check = BuildCheck(out var auth, out _);
        auth.CreateAsyncThrow = new HttpRequestException("token-acquisition-failed");

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = NewRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("BotFrameworkAuthentication", result.Description);
        Assert.Equal("unhealthy", result.Data["botFrameworkAuthentication"]);
    }

    [Fact]
    public async Task CheckHealthAsync_ConversationStoreThrows_ReturnsDegraded()
    {
        var check = BuildCheck(out _, out var store);
        store.GetAllActiveAsyncThrow = new InvalidOperationException("db-down");

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = NewRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("IConversationReferenceStore", result.Description);
        Assert.Equal("unhealthy", result.Data["conversationReferenceStore"]);
    }

    [Fact]
    public async Task CheckHealthAsync_InvalidPolicyOptions_ReturnsDegradedBeforeAuthProbe()
    {
        var messaging = new TeamsMessagingOptions { MicrosoftAppId = "bot-app-id" };
        var policy = new TeamsAppPolicyOptions { AllowedAppCatalogScopes = new List<string>() };
        var auth = new FakeBotFrameworkAuthentication();
        var store = new StubConversationReferenceStore();
        var check = new TeamsAppPolicyHealthCheck(
            WrapInMonitor(messaging),
            WrapInMonitor(policy),
            auth,
            store,
            NullLogger<TeamsAppPolicyHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = NewRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("policy", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(auth.CreateConnectorFactoryCalls);
    }

    [Fact]
    public async Task CheckHealthAsync_CancellationRequested_Throws()
    {
        var check = BuildCheck(out _, out _);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => check.CheckHealthAsync(new HealthCheckContext { Registration = NewRegistration() }, cts.Token));
    }

    [Fact]
    public void Constructor_NullArgs_Throw()
    {
        var messaging = WrapInMonitor(new TeamsMessagingOptions());
        var policy = WrapInMonitor(new TeamsAppPolicyOptions());
        var auth = new FakeBotFrameworkAuthentication();
        var store = new StubConversationReferenceStore();
        var logger = NullLogger<TeamsAppPolicyHealthCheck>.Instance;

        Assert.Throws<ArgumentNullException>(() => new TeamsAppPolicyHealthCheck(null!, policy, auth, store, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsAppPolicyHealthCheck(messaging, null!, auth, store, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsAppPolicyHealthCheck(messaging, policy, null!, store, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsAppPolicyHealthCheck(messaging, policy, auth, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new TeamsAppPolicyHealthCheck(messaging, policy, auth, store, null!));
    }

    private static TeamsAppPolicyHealthCheck BuildCheck(out FakeBotFrameworkAuthentication auth, out StubConversationReferenceStore store)
    {
        var messaging = new TeamsMessagingOptions
        {
            MicrosoftAppId = "bot-app-id",
            AllowedTenantIds = new List<string> { "tenant-1" },
        };
        auth = new FakeBotFrameworkAuthentication();
        store = new StubConversationReferenceStore();
        return new TeamsAppPolicyHealthCheck(
            WrapInMonitor(messaging),
            WrapInMonitor(new TeamsAppPolicyOptions()),
            auth,
            store,
            NullLogger<TeamsAppPolicyHealthCheck>.Instance);
    }

    private static HealthCheckRegistration NewRegistration()
        => new(TeamsAppPolicyHealthCheck.Name, _ => null!, failureStatus: HealthStatus.Degraded, tags: null);
}
