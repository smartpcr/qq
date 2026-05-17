using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Core.Tests;

/// <summary>
/// Pins the <see cref="OutboxRetryEngine"/> tick behaviour: success path acknowledges,
/// transient path reschedules with backoff, permanent path dead-letters immediately,
/// retry budget exhaustion dead-letters, and Retry-After is honoured.
/// </summary>
public sealed class OutboxRetryEngineTests
{
    private static OutboxOptions Options(int maxAttempts = 3) => new()
    {
        PollingIntervalMs = 10,
        BatchSize = 10,
        MaxDegreeOfParallelism = 1,
        MaxAttempts = maxAttempts,
        BaseBackoffSeconds = 2.0,
        MaxBackoffSeconds = 60.0,
        JitterRatio = 0.0,
        RateLimitPerSecond = 1000,
        RateLimitBurst = 1000,
        MeterName = $"test.{Guid.NewGuid():N}",
    };

    [Fact]
    public async Task ProcessOnceAsync_Success_AcknowledgesAndStampsReceipt()
    {
        var options = Options();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var outbox = new RecordingOutbox(NewEntry("e1"));
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Success(new OutboxDeliveryReceipt(
            ActivityId: "act-1",
            ConversationId: "conv-1",
            DeliveredAt: clock.GetUtcNow())));

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            NullLogger<OutboxRetryEngine>.Instance, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        Assert.Single(outbox.Acknowledged);
        Assert.Equal("e1", outbox.Acknowledged[0].Id);
        Assert.Equal("act-1", outbox.Acknowledged[0].Receipt.ActivityId);
        Assert.Empty(outbox.Rescheduled);
        Assert.Empty(outbox.DeadLettered);
    }

    [Fact]
    public async Task ProcessOnceAsync_Transient_ReschedulesWithBackoff()
    {
        var options = Options(maxAttempts: 5);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var outbox = new RecordingOutbox(NewEntry("e1", retryCount: 0));
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Transient("oops"));

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            NullLogger<OutboxRetryEngine>.Instance, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(outbox.Acknowledged);
        Assert.Single(outbox.Rescheduled);
        var (id, next, error) = outbox.Rescheduled[0];
        Assert.Equal("e1", id);
        Assert.Equal("oops", error);
        // attempt 1 with 0 jitter → exactly 2s.
        Assert.Equal(clock.GetUtcNow().AddSeconds(2), next);
    }

    [Fact]
    public async Task ProcessOnceAsync_TransientPlusRetryAfter_PrefersServerHint()
    {
        var options = Options(maxAttempts: 5);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var outbox = new RecordingOutbox(NewEntry("e1", retryCount: 0));
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Transient(
            "rate limited",
            retryAfter: TimeSpan.FromSeconds(45)));

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            NullLogger<OutboxRetryEngine>.Instance, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        Assert.Single(outbox.Rescheduled);
        Assert.Equal(clock.GetUtcNow().AddSeconds(45), outbox.Rescheduled[0].NextRetryAt);
    }

    [Fact]
    public async Task ProcessOnceAsync_Permanent_DeadLettersImmediately()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var outbox = new RecordingOutbox(NewEntry("e1", retryCount: 0));
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Permanent("HTTP 400: bad request"));

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            NullLogger<OutboxRetryEngine>.Instance, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(outbox.Acknowledged);
        Assert.Empty(outbox.Rescheduled);
        Assert.Single(outbox.DeadLettered);
        Assert.Equal("e1", outbox.DeadLettered[0].Id);
        Assert.Equal("HTTP 400: bad request", outbox.DeadLettered[0].Error);
    }

    [Fact]
    public async Task ProcessOnceAsync_TransientAtMaxAttempts_DeadLetters()
    {
        var options = Options(maxAttempts: 3);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        // RetryCount = 2 → nextAttempt would be 3 == MaxAttempts, so dead-letter.
        var outbox = new RecordingOutbox(NewEntry("e1", retryCount: 2));
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Transient("still broken"));

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            NullLogger<OutboxRetryEngine>.Instance, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(outbox.Rescheduled);
        Assert.Single(outbox.DeadLettered);
        Assert.Contains("Retry budget exhausted", outbox.DeadLettered[0].Error);
    }

    [Fact]
    public async Task ProcessOnceAsync_LeakedException_TreatedAsTransient()
    {
        var options = Options(maxAttempts: 5);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var outbox = new RecordingOutbox(NewEntry("e1"));
        var dispatcher = new StubDispatcher(_ => throw new InvalidProgramException("dispatcher bug"));

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            NullLogger<OutboxRetryEngine>.Instance, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        Assert.Single(outbox.Rescheduled);
        Assert.Contains("Unhandled dispatcher exception", outbox.Rescheduled[0].Error);
    }

    [Fact]
    public async Task ProcessOnceAsync_EmptyBatch_ReturnsZero()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var outbox = new RecordingOutbox();
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Success(default));

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            NullLogger<OutboxRetryEngine>.Instance, clock);

        var dispatched = await engine.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(0, dispatched);
    }

    [Fact]
    public void Constructor_RejectsNullCollaborators()
    {
        var opts = Options();
        var metrics = new OutboxMetrics(opts);
        var limiter = new TokenBucketRateLimiter(opts);
        var outbox = new RecordingOutbox();
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Success(default));

        Assert.Throws<ArgumentNullException>(() =>
            new OutboxRetryEngine(null!, dispatcher, opts, metrics, limiter, NullLogger<OutboxRetryEngine>.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new OutboxRetryEngine(outbox, null!, opts, metrics, limiter, NullLogger<OutboxRetryEngine>.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new OutboxRetryEngine(outbox, dispatcher, null!, metrics, limiter, NullLogger<OutboxRetryEngine>.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new OutboxRetryEngine(outbox, dispatcher, opts, null!, limiter, NullLogger<OutboxRetryEngine>.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new OutboxRetryEngine(outbox, dispatcher, opts, metrics, null!, NullLogger<OutboxRetryEngine>.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new OutboxRetryEngine(outbox, dispatcher, opts, metrics, limiter, null!));
    }

    private static OutboxEntry NewEntry(string id, int retryCount = 0) => new()
    {
        OutboxEntryId = id,
        CorrelationId = $"corr-{id}",
        Destination = $"teams://tenant/user/{id}",
        DestinationType = OutboxDestinationTypes.Personal,
        DestinationId = id,
        PayloadType = OutboxPayloadTypes.AgentQuestion,
        PayloadJson = "{}",
        Status = OutboxEntryStatuses.Processing,
        RetryCount = retryCount,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class RecordingOutbox : IMessageOutbox
    {
        private readonly Queue<OutboxEntry> _queue;

        public RecordingOutbox(params OutboxEntry[] entries)
        {
            _queue = new Queue<OutboxEntry>(entries);
        }

        public List<(string Id, OutboxDeliveryReceipt Receipt)> Acknowledged { get; } = new();
        public List<(string Id, OutboxDeliveryReceipt Receipt)> ReceiptsRecorded { get; } = new();
        public List<(string Id, DateTimeOffset NextRetryAt, string Error)> Rescheduled { get; } = new();
        public List<(string Id, string Error)> DeadLettered { get; } = new();
        public List<OutboxEntry> Enqueued { get; } = new();

        public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct)
        {
            Enqueued.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
        {
            var batch = new List<OutboxEntry>();
            while (batch.Count < batchSize && _queue.Count > 0)
            {
                batch.Add(_queue.Dequeue());
            }
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(batch);
        }

        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        {
            Acknowledged.Add((outboxEntryId, receipt));
            return Task.CompletedTask;
        }

        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        {
            ReceiptsRecorded.Add((outboxEntryId, receipt));
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
        {
            Rescheduled.Add((outboxEntryId, nextRetryAt, error));
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
        {
            DeadLettered.Add((outboxEntryId, error));
            return Task.CompletedTask;
        }
    }

    private sealed class StubDispatcher : IOutboxDispatcher
    {
        private readonly Func<OutboxEntry, OutboxDispatchResult> _fn;

        public StubDispatcher(Func<OutboxEntry, OutboxDispatchResult> fn) => _fn = fn;

        public Task<OutboxDispatchResult> DispatchAsync(OutboxEntry entry, CancellationToken ct)
            => Task.FromResult(_fn(entry));
    }
}
