using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Outbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// Pins the enqueue behaviour of <see cref="OutboxBackedProactiveNotifier"/>: every
/// send method snapshots the conversation reference, serializes the payload, and writes
/// a single <see cref="OutboxEntry"/> rather than calling the wrapped notifier.
/// </summary>
public sealed class OutboxBackedProactiveNotifierTests
{
    [Fact]
    public async Task SendProactiveAsync_EnqueuesPersonalEntryWithReferenceSnapshot()
    {
        var store = new RecordingConversationReferenceStore();
        var reference = NewReference(tenantId: "tenant-1", internalUserId: "user-1");
        store.UserReferences[("tenant-1", "user-1")] = reference;

        var outbox = new InMemoryRecordingOutbox();
        var notifier = new OutboxBackedProactiveNotifier(outbox, store,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        var message = SampleMessage("msg-1");
        await notifier.SendProactiveAsync("tenant-1", "user-1", message, CancellationToken.None);

        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal("corr-msg-1", entry.CorrelationId);
        Assert.Equal(OutboxDestinationTypes.Personal, entry.DestinationType);
        Assert.Equal("user-1", entry.DestinationId);
        Assert.Equal("teams://tenant-1/user/user-1", entry.Destination);
        Assert.Equal(OutboxPayloadTypes.MessengerMessage, entry.PayloadType);
        Assert.Equal(reference.ReferenceJson, entry.ConversationReferenceJson);

        var envelope = JsonSerializer.Deserialize<TeamsOutboxPayloadEnvelope>(
            entry.PayloadJson, TeamsOutboxPayloadEnvelope.JsonOptions)!;
        Assert.NotNull(envelope.Message);
        Assert.Equal("msg-1", envelope.Message!.MessageId);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_EnqueuesPersonalQuestion()
    {
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] = NewReference("tenant-1", internalUserId: "user-1");

        var outbox = new InMemoryRecordingOutbox();
        var notifier = new OutboxBackedProactiveNotifier(outbox, store,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await notifier.SendProactiveQuestionAsync(
            "tenant-1",
            "user-1",
            SampleQuestion("q-1", userId: "user-1"),
            CancellationToken.None);

        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal(OutboxPayloadTypes.AgentQuestion, entry.PayloadType);
        Assert.Equal(OutboxDestinationTypes.Personal, entry.DestinationType);
    }

    [Fact]
    public async Task SendToChannelAsync_EnqueuesChannelEntry()
    {
        var store = new RecordingConversationReferenceStore();
        store.ChannelReferences[("tenant-1", "channel-1")] = NewReference("tenant-1", channelId: "channel-1");

        var outbox = new InMemoryRecordingOutbox();
        var notifier = new OutboxBackedProactiveNotifier(outbox, store,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await notifier.SendToChannelAsync(
            "tenant-1", "channel-1", SampleMessage("m-1"), CancellationToken.None);

        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal(OutboxDestinationTypes.Channel, entry.DestinationType);
        Assert.Equal("teams://tenant-1/channel/channel-1", entry.Destination);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_EnqueuesChannelQuestion()
    {
        var store = new RecordingConversationReferenceStore();
        store.ChannelReferences[("tenant-1", "channel-1")] = NewReference("tenant-1", channelId: "channel-1");

        var outbox = new InMemoryRecordingOutbox();
        var notifier = new OutboxBackedProactiveNotifier(outbox, store,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await notifier.SendQuestionToChannelAsync(
            "tenant-1",
            "channel-1",
            SampleQuestion("q-1", channelId: "channel-1"),
            CancellationToken.None);

        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal(OutboxDestinationTypes.Channel, entry.DestinationType);
        Assert.Equal(OutboxPayloadTypes.AgentQuestion, entry.PayloadType);
    }

    [Fact]
    public async Task SendProactiveAsync_ThrowsWhenReferenceMissing()
    {
        var store = new RecordingConversationReferenceStore();
        var notifier = new OutboxBackedProactiveNotifier(
            new InMemoryRecordingOutbox(), store, NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            notifier.SendProactiveAsync("tenant-1", "missing", SampleMessage("m-1"), CancellationToken.None));
    }

    [Fact]
    public async Task SendProactiveAsync_ValidatesArguments()
    {
        var notifier = new OutboxBackedProactiveNotifier(
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(),
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendProactiveAsync("", "user-1", SampleMessage("m-1"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendProactiveAsync("tenant-1", "", SampleMessage("m-1"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            notifier.SendProactiveAsync("tenant-1", "user-1", null!, CancellationToken.None));
    }

    private static TeamsConversationReference NewReference(string tenantId, string? internalUserId = null, string? channelId = null) => new()
    {
        Id = $"ref-{tenantId}-{internalUserId ?? channelId ?? "x"}",
        TenantId = tenantId,
        InternalUserId = internalUserId,
        ChannelId = channelId,
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
}
