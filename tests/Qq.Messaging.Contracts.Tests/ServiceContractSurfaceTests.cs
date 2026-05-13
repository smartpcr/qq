using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

/// <summary>
/// Validates that all service contract interfaces define the expected method signatures.
/// This catches accidental breaking changes to the contract surface.
/// </summary>
public class ServiceContractSurfaceTests
{
    [Fact]
    public void IMessengerConnector_DefinesRequiredMethods()
    {
        var type = typeof(IMessengerConnector);
        Assert.NotNull(type.GetMethod("StartAsync"));
        Assert.NotNull(type.GetMethod("StopAsync"));
        Assert.NotNull(type.GetMethod("SendMessageAsync"));
        Assert.NotNull(type.GetMethod("SendQuestionAsync"));
    }

    [Fact]
    public void IOutboundMessageQueue_DefinesLeaseBasedApi()
    {
        var type = typeof(IOutboundMessageQueue);
        Assert.NotNull(type.GetMethod("EnqueueAsync"));
        Assert.NotNull(type.GetMethod("EnqueueBatchAsync"));
        Assert.NotNull(type.GetMethod("LeaseAsync"));
        Assert.NotNull(type.GetMethod("AcknowledgeAsync"));
        Assert.NotNull(type.GetMethod("ReleaseAsync"));
        Assert.NotNull(type.GetMethod("DeadLetterAsync"));
    }

    [Fact]
    public void IOperatorRegistry_AcceptsPlatformPrincipal()
    {
        var getOp = typeof(IOperatorRegistry).GetMethod("GetOperatorAsync");
        Assert.NotNull(getOp);
        var param = getOp!.GetParameters()[0];
        Assert.Equal(typeof(PlatformPrincipal), param.ParameterType);
    }

    [Fact]
    public void IMessageDeduplicator_KeysOnUpdateId()
    {
        var method = typeof(IMessageDeduplicator).GetMethod("TryMarkProcessedAsync");
        Assert.NotNull(method);
        var param = method!.GetParameters()[0];
        Assert.Equal("platformUpdateId", param.Name);
    }

    [Fact]
    public void ITelegramConnector_ExtendsIMessengerConnector()
    {
        Assert.True(typeof(IMessengerConnector).IsAssignableFrom(typeof(ITelegramConnector)));
        Assert.NotNull(typeof(ITelegramConnector).GetMethod("SetWebhookAsync"));
        Assert.NotNull(typeof(ITelegramConnector).GetMethod("DeleteWebhookAsync"));
    }

    [Fact]
    public void AllAsyncMethods_AcceptCancellationToken()
    {
        var interfaces = new[]
        {
            typeof(IMessengerConnector),
            typeof(ICommandRouter),
            typeof(IInteractionRouter),
            typeof(IOperatorRegistry),
            typeof(IAuditLog),
            typeof(IMessageDeduplicator),
            typeof(IOutboundMessageQueue),
            typeof(ISecretProvider),
            typeof(ITelegramConnector),
            typeof(ITelegramUpdateHandler)
        };

        foreach (var iface in interfaces)
        {
            foreach (var method in iface.GetMethods())
            {
                if (method.ReturnType == typeof(Task) ||
                    (method.ReturnType.IsGenericType &&
                     method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)))
                {
                    var lastParam = method.GetParameters().LastOrDefault();
                    Assert.NotNull(lastParam);
                    Assert.Equal(typeof(CancellationToken), lastParam!.ParameterType);
                }
            }
        }
    }
}
