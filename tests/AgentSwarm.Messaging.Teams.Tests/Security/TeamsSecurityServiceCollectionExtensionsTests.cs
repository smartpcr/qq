using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class TeamsSecurityServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTeamsSecurity_ReplacesDefaultDenyStubs_WithConcreteImplementations()
    {
        var services = NewServiceCollection();
        services.AddSingleton<IIdentityResolver, DefaultDenyIdentityResolver>();
        services.AddSingleton<IUserAuthorizationService, DefaultDenyAuthorizationService>();

        services.AddTeamsSecurity();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        Assert.IsType<EntraIdentityResolver>(sp.GetRequiredService<IIdentityResolver>());
        Assert.IsType<RbacAuthorizationService>(sp.GetRequiredService<IUserAuthorizationService>());
    }

    [Fact]
    public void AddTeamsSecurity_RegistersDefaultDirectoryAndRoleProvider()
    {
        var services = NewServiceCollection();
        services.AddTeamsSecurity();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        Assert.IsType<StaticUserDirectory>(sp.GetRequiredService<IUserDirectory>());
        Assert.IsType<StaticUserRoleProvider>(sp.GetRequiredService<IUserRoleProvider>());
    }

    [Fact]
    public void AddTeamsSecurity_RegistersGateAndMiddlewareSingletons()
    {
        var services = NewServiceCollection();
        services.AddTeamsSecurity();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var middleware1 = sp.GetRequiredService<TenantValidationMiddleware>();
        var middleware2 = sp.GetRequiredService<TenantValidationMiddleware>();
        Assert.Same(middleware1, middleware2);

        var gate1 = sp.GetRequiredService<InstallationStateGate>();
        var gate2 = sp.GetRequiredService<InstallationStateGate>();
        Assert.Same(gate1, gate2);
    }

    [Fact]
    public void AddTeamsSecurity_IsIdempotent_RepeatCallsLeaveOneDescriptorPerType()
    {
        var services = NewServiceCollection();
        services.AddSingleton<IIdentityResolver, DefaultDenyIdentityResolver>();
        services.AddSingleton<IUserAuthorizationService, DefaultDenyAuthorizationService>();

        services.AddTeamsSecurity();
        services.AddTeamsSecurity();
        services.AddTeamsSecurity();

        Assert.Single(services.Where(d => d.ServiceType == typeof(IIdentityResolver)));
        Assert.Single(services.Where(d => d.ServiceType == typeof(IUserAuthorizationService)));
        Assert.Single(services.Where(d => d.ServiceType == typeof(IUserDirectory)));
        Assert.Single(services.Where(d => d.ServiceType == typeof(IUserRoleProvider)));
        Assert.Single(services.Where(d => d.ServiceType == typeof(TenantValidationMiddleware)));
        Assert.Single(services.Where(d => d.ServiceType == typeof(InstallationStateGate)));
        Assert.Single(services.Where(d => d.ServiceType == typeof(TeamsAppPolicyHealthCheck)));
    }

    [Fact]
    public void AddTeamsSecurity_PopulatesRbacDefaultRoleMatrix()
    {
        var services = NewServiceCollection();
        services.AddTeamsSecurity();
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var options = sp.GetRequiredService<IOptionsMonitor<RbacOptions>>().CurrentValue;
        Assert.True(options.RoleCommands.ContainsKey(RbacOptions.OperatorRole));
        Assert.True(options.RoleCommands.ContainsKey(RbacOptions.ApproverRole));
        Assert.True(options.RoleCommands.ContainsKey(RbacOptions.ViewerRole));
    }

    [Fact]
    public void AddTeamsAppPolicyHealthCheck_RegistersHealthCheck()
    {
        var services = NewServiceCollection();
        services.AddTeamsAppPolicyHealthCheck();

        // The health check is registered under the canonical name. Resolve via options
        // because the health-check service builds its registry from
        // HealthCheckServiceOptions.
        using var sp = services.BuildServiceProvider(validateScopes: true);
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        Assert.Contains(options.Registrations, r => r.Name == TeamsAppPolicyHealthCheck.Name);
    }

    [Fact]
    public void AddTeamsSecurity_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TeamsSecurityServiceCollectionExtensions.AddTeamsSecurity(null!));
    }

    [Fact]
    public void AddTeamsAppPolicyHealthCheck_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TeamsSecurityServiceCollectionExtensions.AddTeamsAppPolicyHealthCheck(null!));
    }

    private static IServiceCollection NewServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        // TeamsAppPolicyHealthCheck and TenantValidationMiddleware require options for
        // TeamsMessagingOptions; supply an empty instance so DI validation succeeds.
        services.Configure<AgentSwarm.Messaging.Teams.TeamsMessagingOptions>(_ => { });

        // InstallationStateGate / TenantValidationMiddleware require IConversationReferenceStore,
        // IMessageOutbox, IAuditLogger; supply minimal in-memory stubs so the descriptors
        // resolve when the test builds a provider.
        services.AddSingleton<IConversationReferenceStoreShim>(_ => new IConversationReferenceStoreShim());
        services.AddSingleton<AgentSwarm.Messaging.Teams.IConversationReferenceStore>(
            sp => new StubConversationReferenceStore());
        services.AddSingleton<AgentSwarm.Messaging.Core.IMessageOutbox>(
            sp => new RecordingMessageOutbox());
        services.AddSingleton<AgentSwarm.Messaging.Persistence.IAuditLogger>(
            sp => new RecordingAuditLogger());
        services.AddSingleton<BotFrameworkAuthentication>(sp => new FakeBotFrameworkAuthentication());

        return services;
    }

    /// <summary>Marker so the local lambda factory call site is unambiguous.</summary>
    private sealed class IConversationReferenceStoreShim { }
}
