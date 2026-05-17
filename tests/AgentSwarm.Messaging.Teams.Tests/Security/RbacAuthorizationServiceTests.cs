using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;
using static AgentSwarm.Messaging.Teams.Tests.Security.StaticUserRoleProviderTests;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class RbacAuthorizationServiceTests
{
    private const string Tenant = "tenant-1";
    private const string User = "internal-dave";

    [Fact]
    public async Task AuthorizeAsync_OperatorRunningAgentAsk_IsAuthorized()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var provider = new StubUserRoleProvider().AssignRole(User, RbacOptions.OperatorRole);
        var svc = new RbacAuthorizationService(WrapInMonitor(options), provider, NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, User, "agent ask", CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(RbacOptions.OperatorRole, result.UserRole);
        Assert.Null(result.RequiredRole);
    }

    [Fact]
    public async Task AuthorizeAsync_ViewerRunningApprove_IsRejectedWithApproverAsRequiredRole()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var provider = new StubUserRoleProvider().AssignRole(User, RbacOptions.ViewerRole);
        var svc = new RbacAuthorizationService(WrapInMonitor(options), provider, NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, User, "approve", CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(RbacOptions.ViewerRole, result.UserRole);
        Assert.Equal(RbacOptions.ApproverRole, result.RequiredRole);
    }

    [Fact]
    public async Task AuthorizeAsync_ApproverRunningAgentStatus_IsAuthorized()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var provider = new StubUserRoleProvider().AssignRole(User, RbacOptions.ApproverRole);
        var svc = new RbacAuthorizationService(WrapInMonitor(options), provider, NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, User, "agent status", CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(RbacOptions.ApproverRole, result.UserRole);
    }

    [Fact]
    public async Task AuthorizeAsync_UnmappedUserNoDefaultRole_IsRejectedWithUserRoleNull()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var provider = new StubUserRoleProvider();
        var svc = new RbacAuthorizationService(WrapInMonitor(options), provider, NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, User, "approve", CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Null(result.UserRole);
        Assert.Equal(RbacOptions.ApproverRole, result.RequiredRole);
    }

    [Fact]
    public async Task AuthorizeAsync_UnmappedUserWithViewerDefault_IsAuthorizedForAgentStatus()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.DefaultRole = RbacOptions.ViewerRole;
        var provider = new StubUserRoleProvider();
        var svc = new RbacAuthorizationService(WrapInMonitor(options), provider, NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, User, "agent status", CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(RbacOptions.ViewerRole, result.UserRole);
    }

    [Fact]
    public async Task AuthorizeAsync_ProviderResolvesRole_PrefersProviderOverStaticMap()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.AssignRole(Tenant, User, RbacOptions.ViewerRole);
        var provider = new StubUserRoleProvider().AssignRole(User, RbacOptions.OperatorRole);
        var svc = new RbacAuthorizationService(WrapInMonitor(options), provider, NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, User, "agent ask", CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(RbacOptions.OperatorRole, result.UserRole);
    }

    [Fact]
    public async Task AuthorizeAsync_ProviderReturnsNull_FallsBackToStaticAssignment()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.AssignRole(Tenant, User, RbacOptions.ApproverRole);
        var provider = new StubUserRoleProvider();
        var svc = new RbacAuthorizationService(WrapInMonitor(options), provider, NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, User, "approve", CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(RbacOptions.ApproverRole, result.UserRole);
    }

    [Fact]
    public async Task AuthorizeAsync_EmptyUserId_IsRejected()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var svc = new RbacAuthorizationService(WrapInMonitor(options), new StubUserRoleProvider(), NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, "", "approve", CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Null(result.UserRole);
        Assert.Null(result.RequiredRole);
    }

    [Fact]
    public async Task AuthorizeAsync_EmptyCommand_IsRejected()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var svc = new RbacAuthorizationService(WrapInMonitor(options), new StubUserRoleProvider(), NullLogger<RbacAuthorizationService>.Instance);

        var result = await svc.AuthorizeAsync(Tenant, User, "", CancellationToken.None);

        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizeAsync_CancellationRequested_Throws()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var svc = new RbacAuthorizationService(WrapInMonitor(options), new StubUserRoleProvider(), NullLogger<RbacAuthorizationService>.Instance);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => svc.AuthorizeAsync(Tenant, User, "approve", cts.Token));
    }

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.Throws<ArgumentNullException>(
            () => new RbacAuthorizationService(null!, new StubUserRoleProvider(), NullLogger<RbacAuthorizationService>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new RbacAuthorizationService(WrapInMonitor(options), null!, NullLogger<RbacAuthorizationService>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new RbacAuthorizationService(WrapInMonitor(options), new StubUserRoleProvider(), null!));
    }
}
