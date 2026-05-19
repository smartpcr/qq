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

    // ──────────────────────────────────────────────────────────────────────────
    // Iter-4 evaluator critique #1 — validation/security parity with
    // TeamsProactiveNotifier.SendProactiveQuestionAsync / SendQuestionToChannelAsync.
    // Before this iteration the outbox-backed decorator only validated tenantId /
    // userId / channelId nullness, allowing a caller to enqueue (and pre-save) an
    // AgentQuestion whose TenantId / TargetUserId / TargetChannelId disagreed with
    // the routing handed to the decorator — producing an outbox row delivered
    // under one identity while IAgentQuestionStore persisted the question under
    // another. The Ensure* / ValidateQuestion guards close that regression.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendProactiveQuestionAsync_TenantMismatch_ThrowsAndDoesNotPersistOrEnqueue()
    {
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-a", "user-1")] = NewReference("tenant-a", internalUserId: "user-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        var question = SampleQuestion("q-1", userId: "user-1") with { TenantId = "tenant-b" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendProactiveQuestionAsync("tenant-a", "user-1", question, CancellationToken.None));

        Assert.Equal("tenantId", ex.ParamName);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_UserMismatch_ThrowsAndDoesNotPersistOrEnqueue()
    {
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-A")] = NewReference("tenant-1", internalUserId: "user-A");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        // Question targets user-B but the caller asks to send to user-A.
        var question = SampleQuestion("q-1", userId: "user-B");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendProactiveQuestionAsync("tenant-1", "user-A", question, CancellationToken.None));

        Assert.Equal("userId", ex.ParamName);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_ChannelScopedQuestion_ThrowsScopeViolation()
    {
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] = NewReference("tenant-1", internalUserId: "user-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        // Channel-scoped question routed through the user-scope entry point.
        var question = SampleQuestion("q-1", channelId: "channel-1");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendProactiveQuestionAsync("tenant-1", "user-1", question, CancellationToken.None));

        Assert.Equal("question", ex.ParamName);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_InvalidQuestion_ThrowsAndDoesNotPersistOrEnqueue()
    {
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] = NewReference("tenant-1", internalUserId: "user-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        // Both target fields populated — channel-scope-violation on a user entry point.
        // The shared guards run in the same order as TeamsProactiveNotifier's direct
        // path (tenant → scope → validate), so EnsureScopeUserTargeted fires first
        // because TargetChannelId is set, surfacing ArgumentException bound to
        // "question" before AgentQuestion.Validate() can fire its
        // InvalidOperationException for the same XOR-violation. Pinning the direct
        // path's contract here so both surfaces emit identical exception shapes for
        // identical misuse.
        var invalid = SampleQuestion("q-bad", userId: "user-1") with { TargetChannelId = "channel-1" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendProactiveQuestionAsync("tenant-1", "user-1", invalid, CancellationToken.None));

        Assert.Equal("question", ex.ParamName);
        Assert.Contains("q-bad", ex.Message);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_PayloadInvalidAfterScopeOk_ThrowsInvalidOperation()
    {
        // Sibling of the case above — when tenant and scope guards both pass
        // (the question is correctly user-scoped to the caller-supplied userId
        // and tenant-matched), an AgentQuestion.Validate() failure must surface
        // as InvalidOperationException at the third (payload) guard. Pin both the
        // type and the QuestionId payload so future refactors cannot silently
        // swap guard order without breaking this test.
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] = NewReference("tenant-1", internalUserId: "user-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        // Empty AllowedActions list — AgentQuestion.Validate() rejects this but
        // none of the scope / tenant guards do.
        var invalid = SampleQuestion("q-bad-actions", userId: "user-1") with { AllowedActions = Array.Empty<HumanAction>() };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendProactiveQuestionAsync("tenant-1", "user-1", invalid, CancellationToken.None));

        Assert.Contains("q-bad-actions", ex.Message);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_MissingRequiredField_FailsValidationFirst()
    {
        // Pin Validate() ordering — when the question is otherwise correctly user-scoped
        // and tenant-matched but is missing a required field (CorrelationId), Validate()
        // must reject it BEFORE PreEnqueueSaveQuestionAsync runs.
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] = NewReference("tenant-1", internalUserId: "user-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        var invalid = SampleQuestion("q-bad", userId: "user-1") with { CorrelationId = "" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendProactiveQuestionAsync("tenant-1", "user-1", invalid, CancellationToken.None));

        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_TenantMismatch_ThrowsAndDoesNotPersistOrEnqueue()
    {
        var store = new RecordingConversationReferenceStore();
        store.ChannelReferences[("tenant-a", "channel-1")] = NewReference("tenant-a", channelId: "channel-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        var question = SampleQuestion("q-1", channelId: "channel-1") with { TenantId = "tenant-b" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendQuestionToChannelAsync("tenant-a", "channel-1", question, CancellationToken.None));

        Assert.Equal("tenantId", ex.ParamName);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_ChannelMismatch_ThrowsAndDoesNotPersistOrEnqueue()
    {
        var store = new RecordingConversationReferenceStore();
        store.ChannelReferences[("tenant-1", "channel-A")] = NewReference("tenant-1", channelId: "channel-A");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        // Question targets channel-B but caller sends to channel-A.
        var question = SampleQuestion("q-1", channelId: "channel-B");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendQuestionToChannelAsync("tenant-1", "channel-A", question, CancellationToken.None));

        Assert.Equal("channelId", ex.ParamName);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_UserScopedQuestion_ThrowsScopeViolation()
    {
        var store = new RecordingConversationReferenceStore();
        store.ChannelReferences[("tenant-1", "channel-1")] = NewReference("tenant-1", channelId: "channel-1");

        var outbox = new InMemoryRecordingOutbox();
        var questionStore = new RecordingAgentQuestionStore();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        // User-scoped question routed through the channel-scope entry point.
        var question = SampleQuestion("q-1", userId: "user-1");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            notifier.SendQuestionToChannelAsync("tenant-1", "channel-1", question, CancellationToken.None));

        Assert.Equal("question", ex.ParamName);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_StoredOpenWithMutatedPayload_ThrowsAndDoesNotEnqueue()
    {
        // Iter-4 evaluator critique — EnsureRetryMatchesStoredQuestion parity. The
        // orchestrator mutated the Body between attempts; the stored Open row already
        // exists. The decorator MUST refuse rather than enqueue a card whose payload
        // diverges from the row CardActionHandler will load on approve/reject.
        var store = new RecordingConversationReferenceStore();
        store.UserReferences[("tenant-1", "user-1")] = NewReference("tenant-1", internalUserId: "user-1");

        var questionStore = new RecordingAgentQuestionStore();
        questionStore.Seed(SampleQuestion("q-drift", userId: "user-1")); // original Body = "body"

        var outbox = new InMemoryRecordingOutbox();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        var mutated = SampleQuestion("q-drift", userId: "user-1") with { Body = "body MUTATED" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendProactiveQuestionAsync("tenant-1", "user-1", mutated, CancellationToken.None));

        Assert.Contains("q-drift", ex.Message);
        Assert.Contains("Body", ex.Message);
        Assert.Empty(outbox.Enqueued);
        // No new SaveAsync — the existing seeded row was the only write.
        Assert.Empty(questionStore.SavedQuestions);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_StoredOpenWithMutatedAllowedActions_ThrowsAndDoesNotEnqueue()
    {
        // Channel-scoped sibling — payload mutation on AllowedActions (the
        // orchestrator added a new approve/reject button between retries).
        var store = new RecordingConversationReferenceStore();
        store.ChannelReferences[("tenant-1", "channel-1")] = NewReference("tenant-1", channelId: "channel-1");

        var questionStore = new RecordingAgentQuestionStore();
        questionStore.Seed(SampleQuestion("q-drift-ch", channelId: "channel-1"));

        var outbox = new InMemoryRecordingOutbox();
        var notifier = new OutboxBackedProactiveNotifier(
            outbox, store, questionStore,
            NullLogger<OutboxBackedProactiveNotifier>.Instance);

        var mutated = SampleQuestion("q-drift-ch", channelId: "channel-1") with
        {
            AllowedActions = new[]
            {
                new HumanAction("yes", "Yes", "yes", false),
                new HumanAction("no", "No", "no", false), // newly added button
            },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendQuestionToChannelAsync("tenant-1", "channel-1", mutated, CancellationToken.None));

        Assert.Contains("q-drift-ch", ex.Message);
        Assert.Contains("AllowedActions", ex.Message);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(questionStore.SavedQuestions);
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
