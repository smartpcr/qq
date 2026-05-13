using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

public class TelegramOperatorMappingTests
{
    [Fact]
    public void Mapping_LinksLongChatAndUserIdToOperator()
    {
        var op = new OperatorIdentity("op-1", "tenant-a", "ws-1", "Alice");
        var mapping = new TelegramOperatorMapping(123456789L, 987654321L, op);

        Assert.Equal(123456789L, mapping.ChatId);
        Assert.Equal(987654321L, mapping.UserId);
        Assert.Equal("Alice", mapping.Operator.DisplayName);
        Assert.Equal("tenant-a", mapping.Operator.TenantId);
    }
}
