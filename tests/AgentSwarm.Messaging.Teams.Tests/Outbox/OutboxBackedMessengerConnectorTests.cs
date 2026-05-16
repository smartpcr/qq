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

    [Fact]
    public async Task SendMessageAsync_DuplicateCorrelationAndDestination_SuppressedByDeduplicator()
    {
        // Stage 6.2 test scenario: "Outbound deduplication — Given a message with
        // CorrelationId = c-1 and DestinationId = d-1 was already sent within the
        // dedupe window, When SendMessageAsync is called with the same CorrelationId
        // + DestinationId, Then the duplicate send is suppressed."
        var inner = new RecordingMessengerConnector();
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        var outbox = new InMemoryRecordingOutbox();
        var dedup = new OutboundMessageDeduplicator(
            new OutboundDeduplicationOptions { Window = TimeSpan.FromMinutes(10) },
            TimeProvider.System);

        var decorator = new OutboxBackedMessengerConnector(
            inner, outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: dedup);

        var message = SampleMessage("m-dup");

        await decorator.SendMessageAsync(message, CancellationToken.None);
        await decorator.SendMessageAsync(message, CancellationToken.None);

        // Only the first enqueue lands — the second is dropped by the deduplicator.
        Assert.Single(outbox.Enqueued);
    }

    [Fact]
    public async Task SendMessageAsync_DistinctCorrelations_BothEnqueued()
    {
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        var outbox = new InMemoryRecordingOutbox();
        var dedup = new OutboundMessageDeduplicator();

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(), outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: dedup);

        await decorator.SendMessageAsync(SampleMessage("m-a"), CancellationToken.None);
        await decorator.SendMessageAsync(SampleMessage("m-b"), CancellationToken.None);

        // Each call has a distinct CorrelationId (corr-m-a, corr-m-b) so both land.
        Assert.Equal(2, outbox.Enqueued.Count);
    }

    [Fact]
    public async Task SendMessageAsync_NoDeduplicatorWired_PreservesLegacyBehaviour()
    {
        // Legacy 5-arg constructor — must keep enqueueing every send so pre-Stage-6.2
        // hosts that opted out of the deduplicator continue to work identically.
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        var outbox = new InMemoryRecordingOutbox();

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(), outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance);

        var message = SampleMessage("m-leg");
        await decorator.SendMessageAsync(message, CancellationToken.None);
        await decorator.SendMessageAsync(message, CancellationToken.None);

        Assert.Equal(2, outbox.Enqueued.Count);
    }

    [Fact]
    public async Task SendMessageAsync_EnqueueThrows_RollsBackDedupeEntry_PermitsRetry()
    {
        // Iter-2 evaluator fix #1 — a transient infrastructure failure in the outbox
        // enqueue must NOT poison the dedupe window. Otherwise every retry for the
        // same (CorrelationId, ConversationId) would be silently suppressed until the
        // window naturally elapses.
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        var outbox = new ThrowingOutbox(throwTimes: 1);
        var dedup = new OutboundMessageDeduplicator();

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(), outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: dedup);

        var message = SampleMessage("m-retry");

        // First attempt — outbox throws. The decorator must propagate AND release the
        // dedupe slot.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorator.SendMessageAsync(message, CancellationToken.None));

        Assert.Equal(0, dedup.Count);

        // Second attempt — outbox succeeds. If the dedupe slot were still occupied,
        // the call would silently no-op and outbox.Enqueued would stay empty.
        await decorator.SendMessageAsync(message, CancellationToken.None);

        Assert.Single(outbox.Enqueued);
        Assert.Equal(1, dedup.Count);
    }

    [Fact]
    public async Task SendMessageAsync_ReferenceLookupThrows_RollsBackDedupeEntry_PermitsRetry()
    {
        // Iter-2 evaluator fix #1 — same protection for the reference-lookup failure
        // mode (no TeamsConversationReference registered for the destination). The
        // throw must not poison the dedupe window because the user / sibling pod may
        // have a queued retry that finishes once the reference is registered.
        var router = new RecordingConversationReferenceStore();
        // Intentionally do NOT seed "conv-1" — the lookup will throw.
        var outbox = new InMemoryRecordingOutbox();
        var dedup = new OutboundMessageDeduplicator();

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(), outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: dedup);

        var message = SampleMessage("m-noref");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorator.SendMessageAsync(message, CancellationToken.None));

        Assert.Equal(0, dedup.Count);

        // Reference becomes available — retry must run end-to-end.
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        await decorator.SendMessageAsync(message, CancellationToken.None);

        Assert.Single(outbox.Enqueued);
        Assert.Equal(1, dedup.Count);
    }

    [Fact]
    public async Task SendMessageAsync_ConcurrentRace_WinnerEnqueueFails_LoserStillRunsAndEnqueues()
    {
        // Iter-3 evaluator fix #1 — the regression test the evaluator explicitly
        // requested: "two same-key sends race and the first enqueue fails". Without
        // the in-flight Claim coordination, the loser would observe the winner's
        // registration, return success-shaped immediately, and the winner would then
        // throw and roll back — leaving no outbox row from either call (silently
        // dropped send).
        //
        // Coordination: the first EnqueueAsync call blocks on a gate so the loser has
        // a chance to enter Claim and observe the in-flight winner. Once the gate is
        // released the winner throws (transient failure), triggering the connector's
        // rollback. The loser's WinnerOutcomeTask resolves to `false` and it
        // re-claims as the new owner. The second EnqueueAsync call succeeds.
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        var enqueueGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerEnteredEnqueue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new GatedThrowingOutbox(enqueueGate, winnerEnteredEnqueue);
        var dedup = new OutboundMessageDeduplicator();

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(), outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: dedup);

        var message = SampleMessage("m-race");

        // Winner A starts and blocks inside EnqueueAsync on the gate.
        var aTask = Task.Run(() => decorator.SendMessageAsync(message, CancellationToken.None));

        await winnerEnteredEnqueue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(aTask.IsCompleted);

        // Loser B starts. It must observe A's in-flight registration and AWAIT the
        // outcome (not return success-shaped immediately).
        var bTask = Task.Run(() => decorator.SendMessageAsync(message, CancellationToken.None));

        // Give B time to enter Claim and start awaiting WinnerOutcomeTask. (We can't
        // observe the await directly, but if B were short-circuiting we would see
        // bTask complete here.)
        for (var i = 0; i < 20 && !bTask.IsCompleted; i++)
        {
            await Task.Delay(25);
            if (dedup.Count >= 1)
            {
                break;
            }
        }
        Assert.False(bTask.IsCompleted);

        // Release A's gate — first EnqueueAsync throws. The decorator catches the
        // throw, rolls back the dedupe slot (signals B with `false`), and rethrows.
        enqueueGate.SetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => aTask);

        // B should now re-claim as the new owner and successfully enqueue on the
        // second outbox call.
        await bTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(outbox.Enqueued);
        Assert.Equal(2, outbox.CallCount);
        // After B's commit the dedupe slot is occupied with WinnerOutcomeTask=true so
        // a subsequent same-key send is suppressed.
        Assert.Equal(1, dedup.Count);
    }

    [Fact]
    public async Task SendMessageAsync_ConcurrentRace_WinnerEnqueueSucceeds_LoserSuppressed()
    {
        // Companion to the failure-race test — when the winner succeeds, the loser
        // observes WinnerOutcomeTask=true and suppresses without producing a second
        // outbox row, preserving the original Stage 6.2 dedupe contract.
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        var enqueueGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerEnteredEnqueue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new GatedSucceedingOutbox(enqueueGate, winnerEnteredEnqueue);
        var dedup = new OutboundMessageDeduplicator();

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(), outbox, router,
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: dedup);

        var message = SampleMessage("m-race-success");

        var aTask = Task.Run(() => decorator.SendMessageAsync(message, CancellationToken.None));
        await winnerEnteredEnqueue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(aTask.IsCompleted);

        var bTask = Task.Run(() => decorator.SendMessageAsync(message, CancellationToken.None));

        // Confirm B is waiting and has NOT short-circuited.
        for (var i = 0; i < 10 && !bTask.IsCompleted; i++)
        {
            await Task.Delay(25);
        }
        Assert.False(bTask.IsCompleted);

        enqueueGate.SetResult(true);

        await aTask.WaitAsync(TimeSpan.FromSeconds(5));
        await bTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Exactly one outbox row landed — B was suppressed AFTER A committed.
        Assert.Single(outbox.Enqueued);
        Assert.Equal(1, outbox.CallCount);
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

    /// <summary>
    /// Outbox that throws on the first <paramref name="throwTimes"/> EnqueueAsync calls
    /// then succeeds. Used by the iter-2 evaluator-fix #1 tests to simulate a transient
    /// outbox failure followed by a successful retry.
    /// </summary>
    private sealed class ThrowingOutbox : IMessageOutbox
    {
        private int _remainingThrows;
        public List<OutboxEntry> Enqueued { get; } = new();

        public ThrowingOutbox(int throwTimes) => _remainingThrows = throwTimes;

        public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct)
        {
            if (_remainingThrows > 0)
            {
                _remainingThrows--;
                throw new InvalidOperationException("simulated transient outbox failure");
            }

            Enqueued.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());

        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
            => Task.CompletedTask;

        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
            => Task.CompletedTask;

        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Outbox that signals when the first <c>EnqueueAsync</c> call has been entered,
    /// blocks that first call on a gate so a concurrent loser has a chance to enter
    /// <c>Claim</c> and observe the in-flight winner, then throws when the gate
    /// releases (simulating a transient infrastructure failure that triggers the
    /// connector's rollback path). Subsequent calls succeed.
    /// </summary>
    private sealed class GatedThrowingOutbox : IMessageOutbox
    {
        private readonly TaskCompletionSource<bool> _gate;
        private readonly TaskCompletionSource<bool> _winnerEntered;
        public int CallCount;
        public List<OutboxEntry> Enqueued { get; } = new();

        public GatedThrowingOutbox(
            TaskCompletionSource<bool> gate,
            TaskCompletionSource<bool> winnerEntered)
        {
            _gate = gate;
            _winnerEntered = winnerEntered;
        }

        public async Task EnqueueAsync(OutboxEntry entry, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref CallCount);
            if (call == 1)
            {
                _winnerEntered.TrySetResult(true);
                await _gate.Task;
                throw new InvalidOperationException("simulated transient outbox failure (first call)");
            }

            Enqueued.Add(entry);
        }

        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());

        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
            => Task.CompletedTask;

        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
            => Task.CompletedTask;

        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Outbox variant for the "winner succeeds" race test — signals when the first
    /// <c>EnqueueAsync</c> call has been entered, blocks on a gate so the loser can
    /// observe the in-flight winner, then succeeds when the gate releases.
    /// </summary>
    private sealed class GatedSucceedingOutbox : IMessageOutbox
    {
        private readonly TaskCompletionSource<bool> _gate;
        private readonly TaskCompletionSource<bool> _winnerEntered;
        public int CallCount;
        public List<OutboxEntry> Enqueued { get; } = new();

        public GatedSucceedingOutbox(
            TaskCompletionSource<bool> gate,
            TaskCompletionSource<bool> winnerEntered)
        {
            _gate = gate;
            _winnerEntered = winnerEntered;
        }

        public async Task EnqueueAsync(OutboxEntry entry, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref CallCount);
            if (call == 1)
            {
                _winnerEntered.TrySetResult(true);
                await _gate.Task;
            }

            Enqueued.Add(entry);
        }

        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());

        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
            => Task.CompletedTask;

        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
            => Task.CompletedTask;

        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
            => Task.CompletedTask;
    }
}
