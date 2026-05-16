using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Outbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// Pins the enqueue behaviour of <see cref="OutboxBackedMessengerConnector"/> and the
/// pass-through semantics of <c>ReceiveAsync</c>.
/// </summary>
public sealed class OutboxBackedMessengerConnectorTests
{
    [Fact]
    public async Task SendMessageAsync_EnqueuesAndDoesNotCallInner()
    {
        var inner = new RecordingMessengerConnector();
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        var outbox = new InMemoryRecordingOutbox();

        var decorator = new OutboxBackedMessengerConnector(inner, outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance);

        await decorator.SendMessageAsync(SampleMessage("m-1"), CancellationToken.None);

        Assert.Empty(inner.SentMessages);
        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal(OutboxPayloadTypes.MessengerMessage, entry.PayloadType);
        Assert.Equal("conv-1", entry.DestinationId);
        Assert.Equal("teams://tenant-1/conversation/conv-1", entry.Destination);
    }

    [Fact]
    public async Task SendMessageAsync_ThrowsWhenRouterMissesReference()
    {
        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorator.SendMessageAsync(SampleMessage("m-1"), CancellationToken.None));
    }

    [Fact]
    public async Task SendQuestionAsync_EnqueuesUserScopedQuestion()
    {
        var inner = new RecordingMessengerConnector();
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["user-1"] = NewReference(tenantId: "tenant-1");
        var outbox = new InMemoryRecordingOutbox();

        var decorator = new OutboxBackedMessengerConnector(inner, outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance);

        await decorator.SendQuestionAsync(SampleQuestion("q-1", userId: "user-1"), CancellationToken.None);

        Assert.Empty(inner.SentQuestions);
        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal(OutboxDestinationTypes.Personal, entry.DestinationType);
        Assert.Equal("user-1", entry.DestinationId);

        var envelope = JsonSerializer.Deserialize<TeamsOutboxPayloadEnvelope>(
            entry.PayloadJson, TeamsOutboxPayloadEnvelope.JsonOptions)!;
        Assert.NotNull(envelope.Question);
        Assert.Equal("q-1", envelope.Question!.QuestionId);
    }

    [Fact]
    public async Task SendQuestionAsync_EnqueuesChannelScopedQuestion()
    {
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["channel-1"] = NewReference(tenantId: "tenant-1");
        var outbox = new InMemoryRecordingOutbox();

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(), outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance);

        await decorator.SendQuestionAsync(SampleQuestion("q-1", channelId: "channel-1"), CancellationToken.None);

        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal(OutboxDestinationTypes.Channel, entry.DestinationType);
        Assert.Equal("channel-1", entry.DestinationId);
    }

    [Fact]
    public async Task SendQuestionAsync_InvalidQuestion_ThrowsInvalidOperationException()
    {
        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance);

        // Both target fields null — fails Validate().
        var invalid = SampleQuestion("q-bad", userId: null, channelId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorator.SendQuestionAsync(invalid, CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveAsync_DelegatesToInner()
    {
        var inner = new ReceiveStubConnector();
        var decorator = new OutboxBackedMessengerConnector(
            inner,
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance);

        var received = await decorator.ReceiveAsync(CancellationToken.None);

        Assert.Same(inner.StubEvent, received);
    }

    private static TeamsConversationReference NewReference(string tenantId) => new()
    {
        Id = $"ref-{tenantId}",
        TenantId = tenantId,
        InternalUserId = "user-1",
        ServiceUrl = "https://smba.trafficmanager.net/test/",
        ConversationId = $"conv-{tenantId}",
        BotId = "bot-1",
        ReferenceJson = $"{{\"tenant\":\"{tenantId}\"}}",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static MessengerMessage SampleMessage(string id) => new(
        MessageId: id,
        CorrelationId: $"corr-{id}",
        AgentId: "agent-1",
        TaskId: "task-1",
        ConversationId: "conv-1",
        Body: "hello",
        Severity: MessageSeverities.Info,
        Timestamp: DateTimeOffset.UnixEpoch);

    private static AgentQuestion SampleQuestion(string id, string? userId = null, string? channelId = null) => new()
    {
        QuestionId = id,
        TenantId = "tenant-1",
        TargetUserId = userId,
        TargetChannelId = channelId,
        CorrelationId = $"corr-{id}",
        AgentId = "agent-1",
        TaskId = "task-1",
        Title = "Title",
        Body = "body",
        Severity = MessageSeverities.Info,
        Status = AgentQuestionStatuses.Open,
        AllowedActions = new[] { new HumanAction("yes", "Yes", "yes", false) },
        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class ReceiveStubConnector : IMessengerConnector
    {
        public MessengerEvent StubEvent { get; } = new TextEvent
        {
            EventId = Guid.NewGuid().ToString(),
            CorrelationId = "c1",
            Messenger = "test",
            ExternalUserId = "user-1",
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = "hi",
        };

        public Task SendMessageAsync(MessengerMessage message, CancellationToken ct) => Task.CompletedTask;
        public Task SendQuestionAsync(AgentQuestion question, CancellationToken ct) => Task.CompletedTask;
        public Task<MessengerEvent> ReceiveAsync(CancellationToken ct) => Task.FromResult(StubEvent);
    }
}
