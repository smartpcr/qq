using System.Reflection;

namespace AgentSwarm.Messaging.Abstractions.Tests;

/// <summary>
/// Smoke-checks the surface area of the Stage 1.2 small contracts so that downstream stages
/// (2.1 DI wiring, 2.2 activity handler, 2.3 connector, 3.2 dispatcher) can rely on the
/// expected method shapes.
/// </summary>
public sealed class Stage12ContractShapeTests
{
    [Fact]
    public void ICommandDispatcher_HasDispatchAsync_WithCommandContextAndCancellationToken()
    {
        var method = typeof(ICommandDispatcher).GetMethod(nameof(ICommandDispatcher.DispatchAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(CommandContext), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [Fact]
    public void IInboundEventPublisher_HasPublishAsync_WithMessengerEventAndCancellationToken()
    {
        var method = typeof(IInboundEventPublisher).GetMethod(nameof(IInboundEventPublisher.PublishAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(MessengerEvent), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [Fact]
    public void IIdentityResolver_HasResolveAsync_ReturningNullableUserIdentity()
    {
        var method = typeof(IIdentityResolver).GetMethod(nameof(IIdentityResolver.ResolveAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<UserIdentity?>), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [Fact]
    public void IUserAuthorizationService_HasAuthorizeAsync_ReturningAuthorizationResult()
    {
        var method = typeof(IUserAuthorizationService).GetMethod(nameof(IUserAuthorizationService.AuthorizeAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<AuthorizationResult>), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(string), parameters[1].ParameterType);
        Assert.Equal(typeof(string), parameters[2].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[3].ParameterType);
    }

    [Fact]
    public void IAgentQuestionStore_DeclaresAllSevenRequiredMethods()
    {
        var declared = typeof(IAgentQuestionStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToArray();

        var expected = new[]
        {
            nameof(IAgentQuestionStore.GetByIdAsync),
            nameof(IAgentQuestionStore.GetMostRecentOpenByConversationAsync),
            nameof(IAgentQuestionStore.GetOpenByConversationAsync),
            nameof(IAgentQuestionStore.GetOpenExpiredAsync),
            nameof(IAgentQuestionStore.SaveAsync),
            nameof(IAgentQuestionStore.TryUpdateStatusAsync),
            nameof(IAgentQuestionStore.UpdateConversationIdAsync),
        }.OrderBy(n => n).ToArray();

        Assert.Equal(expected, declared);
    }

    [Fact]
    public void IAgentQuestionStore_TryUpdateStatusAsync_ReturnsBool()
    {
        var method = typeof(IAgentQuestionStore).GetMethod(nameof(IAgentQuestionStore.TryUpdateStatusAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<bool>), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(string), parameters[1].ParameterType);
        Assert.Equal(typeof(string), parameters[2].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[3].ParameterType);
    }

    [Fact]
    public void UserIdentity_IsImmutableRecord_WithFourFields()
    {
        var identity = new UserIdentity(
            InternalUserId: "int-1",
            AadObjectId: "aad-1",
            DisplayName: "Alice",
            Role: "Operator");

        Assert.Equal("int-1", identity.InternalUserId);
        Assert.Equal("aad-1", identity.AadObjectId);
        Assert.Equal("Alice", identity.DisplayName);
        Assert.Equal("Operator", identity.Role);
    }

    [Fact]
    public void AuthorizationResult_IsImmutableRecord_WithExpectedFields()
    {
        var result = new AuthorizationResult(IsAuthorized: true, UserRole: "Approver", RequiredRole: null);

        Assert.True(result.IsAuthorized);
        Assert.Equal("Approver", result.UserRole);
        Assert.Null(result.RequiredRole);
    }

    [Fact]
    public void CommandContext_NormalizedTextIsRequired()
    {
        var context = new CommandContext
        {
            NormalizedText = "agent ask design persistence",
        };

        Assert.Equal("agent ask design persistence", context.NormalizedText);
        Assert.Null(context.ResolvedIdentity);
        Assert.Null(context.TurnContext);
        Assert.Null(context.CorrelationId);
        Assert.Null(context.ConversationId);
        Assert.Null(context.ActivityId);
    }
}
