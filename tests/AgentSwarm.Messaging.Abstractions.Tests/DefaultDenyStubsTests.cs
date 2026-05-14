namespace AgentSwarm.Messaging.Abstractions.Tests;

/// <summary>
/// Verifies the deny-by-default behavior of the Stage 1.2 stub implementations of
/// <see cref="IIdentityResolver"/> and <see cref="IUserAuthorizationService"/>. These stubs
/// are registered in DI before the real RBAC/identity implementations land in Stage 5.1, so
/// they MUST reject every request to ensure no inbound activity slips past the security
/// gates prematurely.
/// </summary>
public sealed class DefaultDenyStubsTests
{
    [Fact]
    public async Task DefaultDenyIdentityResolver_ReturnsNull_ForAnyAadObjectId()
    {
        IIdentityResolver resolver = new DefaultDenyIdentityResolver();

        var result = await resolver.ResolveAsync("aad-12345", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DefaultDenyIdentityResolver_ReturnsNull_ForEmptyInput()
    {
        IIdentityResolver resolver = new DefaultDenyIdentityResolver();

        var result = await resolver.ResolveAsync(string.Empty, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DefaultDenyIdentityResolver_HonorsCancellation()
    {
        IIdentityResolver resolver = new DefaultDenyIdentityResolver();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync("aad", cts.Token));
    }

    [Fact]
    public async Task DefaultDenyAuthorizationService_ReturnsUnauthorized_ForAnyInput()
    {
        IUserAuthorizationService service = new DefaultDenyAuthorizationService();

        var result = await service.AuthorizeAsync(
            tenantId: "tenant",
            userId: "user",
            command: "approve",
            ct: CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Null(result.UserRole);
        Assert.Null(result.RequiredRole);
    }

    [Theory]
    [InlineData("agent ask")]
    [InlineData("agent status")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("escalate")]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task DefaultDenyAuthorizationService_RejectsEveryCanonicalCommand(string command)
    {
        IUserAuthorizationService service = new DefaultDenyAuthorizationService();

        var result = await service.AuthorizeAsync("tenant", "user", command, CancellationToken.None);

        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task DefaultDenyAuthorizationService_HonorsCancellation()
    {
        IUserAuthorizationService service = new DefaultDenyAuthorizationService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.AuthorizeAsync("t", "u", "approve", cts.Token));
    }
}
