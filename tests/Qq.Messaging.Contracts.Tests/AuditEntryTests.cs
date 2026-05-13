using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

public class AuditEntryTests
{
    [Fact]
    public void AuditEntry_CapturesPlatformIdentifiers()
    {
        var entry = new AuditEntry(
            EntryId: "audit-1",
            MessageId: "msg-42",
            OperatorId: "op-1",
            AgentId: "agent-7",
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: "corr-abc",
            Direction: MessageDirection.Inbound,
            PlatformChatId: "12345",
            PlatformUserId: "67890",
            PlatformUpdateId: "update-999",
            Payload: "{\"text\":\"/approve\"}");

        Assert.Equal("12345", entry.PlatformChatId);
        Assert.Equal("67890", entry.PlatformUserId);
        Assert.Equal("update-999", entry.PlatformUpdateId);
        Assert.Equal(MessageDirection.Inbound, entry.Direction);
    }
}
