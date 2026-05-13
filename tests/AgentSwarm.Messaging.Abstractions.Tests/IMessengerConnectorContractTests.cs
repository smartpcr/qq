using System.Reflection;

namespace AgentSwarm.Messaging.Abstractions.Tests;

/// <summary>
/// Stage 1.2 test scenario: "Interface contract completeness — Given the
/// <see cref="IMessengerConnector"/> interface, When inspected via reflection, Then it
/// contains exactly <c>SendMessageAsync</c>, <c>SendQuestionAsync</c>, and
/// <c>ReceiveAsync</c> methods."
/// </summary>
public sealed class IMessengerConnectorContractTests
{
    private static MethodInfo[] DeclaredMethods()
        => typeof(IMessengerConnector).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    [Fact]
    public void IMessengerConnector_DeclaresExactlyThreeMethods()
    {
        var methods = DeclaredMethods();

        Assert.Equal(3, methods.Length);
    }

    [Fact]
    public void IMessengerConnector_DeclaresSendMessageAsync_SendQuestionAsync_ReceiveAsync()
    {
        var methodNames = DeclaredMethods().Select(m => m.Name).OrderBy(n => n).ToArray();

        Assert.Equal(
            new[] { "ReceiveAsync", "SendMessageAsync", "SendQuestionAsync" },
            methodNames);
    }

    [Fact]
    public void SendMessageAsync_HasMessengerMessageAndCancellationTokenParameters_AndReturnsTask()
    {
        var method = typeof(IMessengerConnector).GetMethod(nameof(IMessengerConnector.SendMessageAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(MessengerMessage), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [Fact]
    public void SendQuestionAsync_HasAgentQuestionAndCancellationTokenParameters_AndReturnsTask()
    {
        var method = typeof(IMessengerConnector).GetMethod(nameof(IMessengerConnector.SendQuestionAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(AgentQuestion), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [Fact]
    public void ReceiveAsync_HasCancellationTokenParameter_AndReturnsTaskOfMessengerEvent()
    {
        var method = typeof(IMessengerConnector).GetMethod(nameof(IMessengerConnector.ReceiveAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<MessengerEvent>), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(CancellationToken), parameters[0].ParameterType);
    }
}
