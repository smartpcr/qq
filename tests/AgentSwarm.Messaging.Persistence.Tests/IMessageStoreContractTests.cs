using System.Reflection;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Persistence.Tests;

/// <summary>
/// Stage 1.3 test scenario: "Correlation query contract — Given <c>IMessageStore</c>, When
/// inspected, Then <c>GetByCorrelationIdAsync</c> accepts a <c>string correlationId</c> and
/// returns a list of messages." Also asserts the inbound/outbound save signatures so
/// downstream stages depending on this contract have a regression guard.
/// </summary>
public sealed class IMessageStoreContractTests
{
    [Fact]
    public void IMessageStore_GetByCorrelationIdAsync_AcceptsStringAndReturnsListOfMessages()
    {
        var method = typeof(IMessageStore).GetMethod(nameof(IMessageStore.GetByCorrelationIdAsync));
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal("correlationId", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);

        // Return type must be Task<IReadOnlyList<PersistedMessage>> — i.e., a list of messages.
        var returnType = method.ReturnType;
        Assert.True(returnType.IsGenericType);
        Assert.Equal(typeof(Task<>), returnType.GetGenericTypeDefinition());

        var taskPayload = returnType.GetGenericArguments().Single();
        Assert.True(taskPayload.IsGenericType);
        Assert.Equal(typeof(IReadOnlyList<>), taskPayload.GetGenericTypeDefinition());

        var elementType = taskPayload.GetGenericArguments().Single();
        Assert.Equal(typeof(PersistedMessage), elementType);
    }

    [Fact]
    public void IMessageStore_SaveInboundAsync_AcceptsMessengerEventAndCancellationToken()
    {
        var method = typeof(IMessageStore).GetMethod(nameof(IMessageStore.SaveInboundAsync));
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(MessengerEvent), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    [Fact]
    public void IMessageStore_SaveOutboundAsync_AcceptsMessengerMessageAndCancellationToken()
    {
        var method = typeof(IMessageStore).GetMethod(nameof(IMessageStore.SaveOutboundAsync));
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(MessengerMessage), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    [Fact]
    public void IMessageStore_ExposesExactlyTheThreeContractMethods()
    {
        var methods = typeof(IMessageStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(IMessageStore.GetByCorrelationIdAsync),
                nameof(IMessageStore.SaveInboundAsync),
                nameof(IMessageStore.SaveOutboundAsync),
            },
            methods);
    }

    [Fact]
    public void IAuditLogger_LogAsync_AcceptsAuditEntryAndCancellationToken()
    {
        var method = typeof(IAuditLogger).GetMethod(nameof(IAuditLogger.LogAsync));
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(AuditEntry), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.Equal(typeof(Task), method.ReturnType);
    }
}
