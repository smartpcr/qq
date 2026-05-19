using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Outbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// Pins the enqueue behaviour of <see cref="OutboxBackedProactiveNotifier"/>: every
/// send method snapshots the conversation reference, serializes the payload, and writes
/// a single <see cref="OutboxEntry"/> rather than calling the wrapped notifier. The
/// AgentQuestion-bearing paths additionally persist the question to
/// <see cref="IAgentQuestionStore"/> BEFORE enqueueing so the OutboxRetryEngine /
/// CardActionHandler can resolve the row immediately on user action — see
/// <c>implementation-plan.md</c> §6.1 and the class XML remarks for the rationale.
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
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(outbox, store, questionStore,
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

        // MessengerMessage path must NOT touch the AgentQuestion store.
        Assert.Empty(questionStore.SavedQuestions);
        Assert.Empty(questionStore.GetByIdCalls);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_EnqueuesPersonalQuestion()
    {
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] = NewReference("tenant-1", internalUserId: "user-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(outbox, store, questionStore,
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
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await notifier.SendToChannelAsync(
            "tenant-1", "channel-1", SampleMessage("m-1"), CancellationToken.None);

        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal(OutboxDestinationTypes.Channel, entry.DestinationType);
        Assert.Equal("teams://tenant-1/channel/channel-1", entry.Destination);

        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_EnqueuesChannelQuestion()
    {
        var store = new RecordingConversationReferenceStore();
        store.ChannelReferences[("tenant-1", "channel-1")] = NewReference("tenant-1", channelId: "channel-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(outbox, store, questionStore,
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
            new InMemoryRecordingOutbox(), store, new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            notifier.SendProactiveAsync("tenant-1", "missing", SampleMessage("m-1"), CancellationToken.None));
    }

    [Fact]
    public async Task SendProactiveAsync_ValidatesArguments()
    {
        var notifier = new OutboxBackedProactiveNotifier(
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(),
            new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendProactiveAsync("", "user-1", SampleMessage("m-1"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendProactiveAsync("tenant-1", "", SampleMessage("m-1"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            notifier.SendProactiveAsync("tenant-1", "user-1", null!, CancellationToken.None));
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_PersistsSanitisedQuestionBeforeEnqueue()
    {
        // implementation-plan.md §6.1: "The send method calls IAgentQuestionStore.SaveAsync(question)
        // first, then calls IMessageOutbox.EnqueueAsync … so that CardActionHandler can resolve the
        // full AgentQuestion via IAgentQuestionStore.GetByIdAsync(questionId) immediately after the
        // user interacts with the card — even if the card is delivered and acted upon within
        // milliseconds. Without this pre-enqueue save, a race condition exists." This test pins both
        // the ordering and the sanitisation (ConversationId blanked, Status forced to Open).
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] =
            NewReference("tenant-1", internalUserId: "user-1");

        var questionStore = new RecordingAgentQuestionStore();
        var outbox = new OrderingTrackingOutbox(questionStore.OperationLog);

        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        // Caller hands in a question that already carries a stale ConversationId — the
        // decorator must blank it before SaveAsync so a later
        // SqlAgentQuestionStore.UpdateConversationIdAsync gets to write the real value.
        var question = SampleQuestion("q-pre-1", userId: "user-1") with
        {
            ConversationId = "stale-conv-id",
        };

        await notifier.SendProactiveQuestionAsync(
            "tenant-1", "user-1", question, CancellationToken.None);

        var saved = Assert.Single(questionStore.SavedQuestions);
        Assert.Equal("q-pre-1", saved.QuestionId);
        Assert.Null(saved.ConversationId);
        Assert.Equal(AgentQuestionStatuses.Open, saved.Status);

        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal(OutboxPayloadTypes.AgentQuestion, entry.PayloadType);

        // Ordering: SaveAsync MUST land before EnqueueAsync (otherwise the race the §6.1
        // narrative warns about reappears).
        Assert.Equal(
            new[] { "SaveAsync:q-pre-1", "EnqueueAsync:AgentQuestion" },
            questionStore.OperationLog);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_DuplicateOpenQuestion_SkipsSaveAndEnqueues()
    {
        // Retry / replay case: orchestrator calls SendProactiveQuestionAsync a second time
        // for the same questionId after the prior outbox enqueue dead-lettered or after a
        // pod recycle. SaveAsync MUST be skipped (rather than overwrite or throw) so the
        // outbox engine can deliver the original card without resetting status.
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] =
            NewReference("tenant-1", internalUserId: "user-1");

        var questionStore = new RecordingAgentQuestionStore();
        questionStore.Seed(SampleQuestion("q-dup", userId: "user-1"));

        var outbox = new InMemoryRecordingOutbox();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        await notifier.SendProactiveQuestionAsync(
            "tenant-1", "user-1", SampleQuestion("q-dup", userId: "user-1"),
            CancellationToken.None);

        Assert.Empty(questionStore.SavedQuestions);
        Assert.Single(outbox.Enqueued);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_TerminalStoredStatus_ThrowsAndSkipsEnqueue()
    {
        // Safety net: a prior delivery already resolved the question. The orchestrator
        // attempting a retry MUST be told (loudly) rather than ship a stale card asking the
        // user to approve / reject something already actioned.
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] =
            NewReference("tenant-1", internalUserId: "user-1");

        var questionStore = new RecordingAgentQuestionStore();
        questionStore.Seed(SampleQuestion("q-term", userId: "user-1") with
        {
            Status = AgentQuestionStatuses.Resolved,
        });

        var outbox = new InMemoryRecordingOutbox();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendProactiveQuestionAsync(
                "tenant-1", "user-1",
                SampleQuestion("q-term", userId: "user-1"),
                CancellationToken.None));

        Assert.Contains("q-term", ex.Message);
        Assert.Contains("terminal", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_PersistsBeforeEnqueueAndIsIdempotent()
    {
        // Channel-scoped sibling of SendProactiveQuestionAsync_PersistsSanitisedQuestionBeforeEnqueue.
        var store = new RecordingConversationReferenceStore();
        store.ChannelReferences[("tenant-1", "channel-1")] =
            NewReference("tenant-1", channelId: "channel-1");

        var questionStore = new RecordingAgentQuestionStore();
        var outbox = new OrderingTrackingOutbox(questionStore.OperationLog);
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        var question = SampleQuestion("q-ch-1", channelId: "channel-1") with
        {
            ConversationId = "stale-conv-id",
        };

        await notifier.SendQuestionToChannelAsync(
            "tenant-1", "channel-1", question, CancellationToken.None);

        var saved = Assert.Single(questionStore.SavedQuestions);
        Assert.Null(saved.ConversationId);
        Assert.Equal(AgentQuestionStatuses.Open, saved.Status);

        Assert.Equal(
            new[] { "SaveAsync:q-ch-1", "EnqueueAsync:AgentQuestion" },
            questionStore.OperationLog);

        // Replay the same enqueue — the seeded Open row must short-circuit SaveAsync but
        // still produce a second outbox entry (the orchestrator may legitimately request a
        // redelivery after a dead-letter).
        await notifier.SendQuestionToChannelAsync(
            "tenant-1", "channel-1",
            SampleQuestion("q-ch-1", channelId: "channel-1"),
            CancellationToken.None);

        Assert.Single(questionStore.SavedQuestions);
        Assert.Equal(2, outbox.Enqueued.Count);
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
