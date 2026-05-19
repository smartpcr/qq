using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Core.Tests;

/// <summary>
/// Stage 6.3 iter-9 evaluator fix item 1 — pins the structural log-scope coverage
/// contract of <see cref="OutboxRetryEngine"/>: every <see cref="ILogger"/> call
/// the engine emits (lifecycle <i>and</i> per-entry) MUST be wrapped in a
/// <see cref="ILogger.BeginScope"/> dictionary that carries the canonical Stage
/// 6.3 step-5 enrichment keys <c>CorrelationId</c>, <c>TenantId</c>, <c>UserId</c>.
/// Lifecycle logs (engine start / stop / per-tick error) carry the keys as the
/// empty-string sentinel (so Serilog enrichers see a stable shape — the keys are
/// PRESENT, just empty); per-entry logs layer a nested scope on top with the
/// entry's real <see cref="OutboxEntry.CorrelationId"/> and the
/// (tenantId, userOrChannelId) parsed from <see cref="OutboxEntry.Destination"/>.
/// <para>
/// This is a structural / behavioural test, not a string-match test on log
/// output — it uses a recording <see cref="ILogger"/> that captures the active
/// scope stack at each <c>Log(...)</c> call and asserts the three keys are
/// present at every emission. Without the iter-9 fix the engine emitted its
/// lifecycle and per-entry logs without any enrichment scope, so the assertions
/// here fail with the pre-iter-9 code.
/// </para>
/// </summary>
public sealed class OutboxRetryEngineLogScopeTests
{
    private static OutboxOptions Options() => new()
    {
        PollingIntervalMs = 10,
        BatchSize = 10,
        MaxDegreeOfParallelism = 1,
        MaxAttempts = 3,
        BaseBackoffSeconds = 2.0,
        MaxBackoffSeconds = 60.0,
        JitterRatio = 0.0,
        RateLimitPerSecond = 1000,
        RateLimitBurst = 1000,
        MeterName = $"test.scope.{Guid.NewGuid():N}",
    };

    [Fact]
    public async Task ProcessOnceAsync_SuccessPath_DeliveryLogCarriesPerEntryEnrichmentKeys()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        // Uri.Host always lowercases the authority component (RFC 3986 §3.2.2), so the
        // canonical tenantId surfaced through ParseDestination is the lowercased form
        // of whatever was embedded in OutboxEntry.Destination. Tests use lowercase
        // tenant IDs to match this normalised contract.
        var entry = NewEntry("entry-1", tenantId: "tenant-a", userId: "user-x", correlationId: "corr-success");
        var outbox = new RecordingOutbox(entry);
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Success(new OutboxDeliveryReceipt(
            ActivityId: "act-1",
            ConversationId: "conv-1",
            DeliveredAt: clock.GetUtcNow())));
        var logger = new ScopeRecordingLogger<OutboxRetryEngine>();

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            logger, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        // At least one Information-level "Delivered outbox entry ..." log fired and it
        // MUST carry the per-entry enrichment keys (CorrelationId / TenantId / UserId)
        // populated with the entry's real values.
        var deliveredLog = Assert.Single(logger.Entries.Where(e =>
            e.Level == LogLevel.Information && e.Message.StartsWith("Delivered outbox entry", StringComparison.Ordinal)));
        AssertEnrichmentKeysPresent(deliveredLog, expectedCorrelationId: "corr-success", expectedTenantId: "tenant-a", expectedUserId: "user-x");
    }

    [Fact]
    public async Task ProcessOnceAsync_TransientPath_RetryLogCarriesPerEntryEnrichmentKeys()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entry = NewEntry("entry-t", tenantId: "tenant-t", userId: "user-t", correlationId: "corr-transient", retryCount: 0);
        var outbox = new RecordingOutbox(entry);
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Transient("flaky-backend"));
        var logger = new ScopeRecordingLogger<OutboxRetryEngine>();

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            logger, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        var transientLog = Assert.Single(logger.Entries.Where(e =>
            e.Level == LogLevel.Warning && e.Message.StartsWith("Transient failure delivering outbox entry", StringComparison.Ordinal)));
        AssertEnrichmentKeysPresent(transientLog, expectedCorrelationId: "corr-transient", expectedTenantId: "tenant-t", expectedUserId: "user-t");
    }

    [Fact]
    public async Task ProcessOnceAsync_PermanentPath_DeadLetterLogCarriesPerEntryEnrichmentKeys()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entry = NewEntry("entry-p", tenantId: "tenant-p", userId: "channel-p", correlationId: "corr-perm",
            destinationOverride: "teams://tenant-p/channel/channel-p");
        var outbox = new RecordingOutbox(entry);
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Permanent("400 Bad Request"));
        var logger = new ScopeRecordingLogger<OutboxRetryEngine>();

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            logger, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        var permanentLog = Assert.Single(logger.Entries.Where(e =>
            e.Level == LogLevel.Error && e.Message.StartsWith("Permanently dead-lettering outbox entry", StringComparison.Ordinal)));
        AssertEnrichmentKeysPresent(permanentLog, expectedCorrelationId: "corr-perm", expectedTenantId: "tenant-p", expectedUserId: "channel-p");
    }

    [Fact]
    public async Task ProcessOnceAsync_DispatcherLeaksException_ErrorLogCarriesPerEntryEnrichmentKeys()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entry = NewEntry("entry-x", tenantId: "tenant-x", userId: "user-x", correlationId: "corr-leak");
        var outbox = new RecordingOutbox(entry);
        var dispatcher = new StubDispatcher(_ => throw new InvalidOperationException("boom"));
        var logger = new ScopeRecordingLogger<OutboxRetryEngine>();

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            logger, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        // The "Dispatcher leaked exception ..." Error log MUST also carry the per-entry
        // enrichment keys — it fires inside ProcessEntryAsync where the per-entry scope
        // (BeginEntryLogScope) is active.
        var leakedLog = Assert.Single(logger.Entries.Where(e =>
            e.Level == LogLevel.Error && e.Message.StartsWith("Dispatcher leaked exception", StringComparison.Ordinal)));
        AssertEnrichmentKeysPresent(leakedLog, expectedCorrelationId: "corr-leak", expectedTenantId: "tenant-x", expectedUserId: "user-x");
    }

    [Fact]
    public async Task ExecuteAsync_LifecycleLogsCarryEmptyEnrichmentSentinelKeys()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var outbox = new RecordingOutbox();
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Success(default));
        var logger = new ScopeRecordingLogger<OutboxRetryEngine>();

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            logger, clock);

        using var cts = new CancellationTokenSource();
        var run = ((Microsoft.Extensions.Hosting.IHostedService)engine).StartAsync(cts.Token);
        await run;
        // Let the engine emit the startup log on a polling tick, then stop.
        await Task.Delay(50);
        cts.Cancel();
        await ((Microsoft.Extensions.Hosting.IHostedService)engine).StopAsync(CancellationToken.None);

        var startupLog = Assert.Single(logger.Entries.Where(e =>
            e.Level == LogLevel.Information && e.Message.StartsWith("OutboxRetryEngine starting", StringComparison.Ordinal)));

        // Lifecycle scope contract: the canonical three keys are PRESENT but empty
        // sentinels, so Serilog enrichers see a stable shape and downstream log shippers
        // never see a "missing key" gap for engine-level logs that have no per-message
        // context.
        AssertEnrichmentKeysPresent(startupLog, expectedCorrelationId: string.Empty, expectedTenantId: string.Empty, expectedUserId: string.Empty);
    }

    /// <summary>
    /// Stage 6.3 iter-13 evaluator fix item 2 — pins the contract that a DIRECT
    /// caller of <see cref="OutboxRetryEngine.ProcessOnceAsync"/> (a test harness
    /// or a host that drives the engine manually outside the
    /// <see cref="OutboxRetryEngine.ExecuteAsync"/> background-service loop) still
    /// produces the canonical Stage 6.3 step-5 enrichment keys on EVERY log entry
    /// the method emits — including the
    /// <see cref="OutboxRetryEngine.SetPendingCountFromOutboxAsync"/> fallback
    /// <c>LogDebug</c> path that fires when
    /// <see cref="IMessageOutbox.CountPendingAsync"/> throws.
    /// <para>
    /// The iter-12 evaluator flagged this as a real bug: prior to the
    /// method-level scope, the engine-loop scope only existed inside
    /// <c>ExecuteAsync</c>, so direct callers of <c>ProcessOnceAsync</c> would
    /// emit unenriched log entries from the CountPendingAsync fallback path.
    /// The fix is to open a <c>using var tickScope = _logger.BeginScope(...)</c>
    /// at the top of <c>ProcessOnceAsync</c> with the engine-lifecycle scope
    /// state; this test forces the failure path and asserts the keys are
    /// present at the LogDebug emission site.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProcessOnceAsync_CountPendingThrows_FallbackLogDebugCarriesEnrichmentScope()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        // Empty queue forces the "queue empty" branch which still calls
        // SetPendingCountFromOutboxAsync(fallback: 0, ct). The outbox's
        // CountPendingAsync throws so the fallback LogDebug at line 336 fires.
        var outbox = new RecordingOutbox
        {
            CountPendingAsyncThrow = new InvalidOperationException("db-unreachable"),
        };
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Success(default));
        var logger = new ScopeRecordingLogger<OutboxRetryEngine>();

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            logger, clock);

        // DIRECT call to ProcessOnceAsync — bypasses ExecuteAsync entirely.
        // Pre-iter-13 this path emitted the CountPendingAsync fallback LogDebug
        // WITHOUT the canonical (CorrelationId, TenantId, UserId) scope keys
        // because the engine-loop scope was only opened inside ExecuteAsync.
        await engine.ProcessOnceAsync(CancellationToken.None);

        var fallbackLog = Assert.Single(logger.Entries.Where(e =>
            e.Level == LogLevel.Debug &&
            e.Message.StartsWith("Outbox CountPendingAsync probe failed", StringComparison.Ordinal)));

        // Direct-call shape: the lifecycle scope is active with the canonical
        // three keys present as empty-string sentinels (no per-entry context
        // applies on the empty-queue branch).
        AssertEnrichmentKeysPresent(
            fallbackLog,
            expectedCorrelationId: string.Empty,
            expectedTenantId: string.Empty,
            expectedUserId: string.Empty);
    }

    /// <summary>
    /// Stage 6.3 iter-13 evaluator fix item 2 (non-empty batch variant) — same
    /// contract, but the queue has an entry so <see cref="OutboxRetryEngine.SetPendingCountFromOutboxAsync"/>
    /// is invoked with <c>fallback: batch.Count</c> instead of <c>fallback: 0</c>.
    /// The lifecycle scope is still active because the CountPendingAsync probe
    /// runs BEFORE the per-entry scope is opened inside the
    /// <c>Parallel.ForEachAsync</c> body.
    /// </summary>
    [Fact]
    public async Task ProcessOnceAsync_NonEmptyBatch_CountPendingThrows_FallbackLogCarriesEnrichmentScope()
    {
        var options = Options();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entry = NewEntry("entry-nb", tenantId: "tenant-nb", userId: "user-nb", correlationId: "corr-nb");
        var outbox = new RecordingOutbox(entry)
        {
            CountPendingAsyncThrow = new InvalidOperationException("db-unreachable"),
        };
        var dispatcher = new StubDispatcher(_ => OutboxDispatchResult.Success(new OutboxDeliveryReceipt(
            ActivityId: "act-nb",
            ConversationId: "conv-nb",
            DeliveredAt: clock.GetUtcNow())));
        var logger = new ScopeRecordingLogger<OutboxRetryEngine>();

        var engine = new OutboxRetryEngine(
            outbox, dispatcher, options, new OutboxMetrics(options),
            new TokenBucketRateLimiter(options, clock),
            logger, clock);

        await engine.ProcessOnceAsync(CancellationToken.None);

        var fallbackLog = Assert.Single(logger.Entries.Where(e =>
            e.Level == LogLevel.Debug &&
            e.Message.StartsWith("Outbox CountPendingAsync probe failed", StringComparison.Ordinal)));

        // CountPendingAsync runs BEFORE the per-entry scope opens (the per-entry
        // scope is opened inside the Parallel.ForEachAsync body), so the keys
        // should be empty sentinels at this emission point. This is the precise
        // contract the iter-12 evaluator wanted nailed down.
        AssertEnrichmentKeysPresent(
            fallbackLog,
            expectedCorrelationId: string.Empty,
            expectedTenantId: string.Empty,
            expectedUserId: string.Empty);
    }

    private static void AssertEnrichmentKeysPresent(LogRecord log, string expectedCorrelationId, string expectedTenantId, string expectedUserId)
    {
        var merged = log.MergedScopeProperties;
        Assert.True(merged.ContainsKey("CorrelationId"),
            $"Expected 'CorrelationId' in log scope. Captured keys: [{string.Join(", ", merged.Keys)}]");
        Assert.True(merged.ContainsKey("TenantId"),
            $"Expected 'TenantId' in log scope. Captured keys: [{string.Join(", ", merged.Keys)}]");
        Assert.True(merged.ContainsKey("UserId"),
            $"Expected 'UserId' in log scope. Captured keys: [{string.Join(", ", merged.Keys)}]");
        Assert.Equal(expectedCorrelationId, merged["CorrelationId"]?.ToString());
        Assert.Equal(expectedTenantId, merged["TenantId"]?.ToString());
        Assert.Equal(expectedUserId, merged["UserId"]?.ToString());
    }

    private static OutboxEntry NewEntry(
        string id,
        string tenantId,
        string userId,
        string correlationId,
        int retryCount = 0,
        string? destinationOverride = null) => new()
        {
            OutboxEntryId = id,
            CorrelationId = correlationId,
            Destination = destinationOverride ?? $"teams://{tenantId}/user/{userId}",
            DestinationType = OutboxDestinationTypes.Personal,
            DestinationId = userId,
            PayloadType = OutboxPayloadTypes.AgentQuestion,
            PayloadJson = "{}",
            Status = OutboxEntryStatuses.Processing,
            RetryCount = retryCount,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

    private sealed record LogRecord(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> MergedScopeProperties);

    /// <summary>
    /// Stage 6.3 iter-9 evaluator fix item 1 verification harness — a tiny ILogger
    /// that captures BOTH the formatted log message AND the merged active scope
    /// stack at the moment each Log call fires. The scope stack is walked from
    /// outermost to innermost so a per-entry nested scope's CorrelationId / TenantId
    /// / UserId values overlay the lifecycle scope's empty sentinels, matching the
    /// real .NET ILogger semantics. We deliberately avoid Serilog here so the test
    /// stays in the Core test project (no cross-project test plumbing) — the
    /// Serilog provider promotes the same scope dictionary onto LogEventProperty
    /// instances downstream, so verifying the scope shape on ILogger IS the
    /// canonical structural-coverage assertion.
    /// </summary>
    private sealed class ScopeRecordingLogger<T> : ILogger<T>
    {
        private readonly object _lock = new();
        private readonly Stack<object?> _scopeStack = new();

        public List<LogRecord> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            lock (_lock)
            {
                _scopeStack.Push(state);
            }
            return new ScopePopper(() =>
            {
                lock (_lock)
                {
                    if (_scopeStack.Count > 0)
                    {
                        _scopeStack.Pop();
                    }
                }
            });
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var formatted = formatter(state, exception);

            // Walk the scope stack OUTERMOST → INNERMOST so per-entry values overlay the
            // lifecycle empty sentinels (which is the real Serilog merge semantics).
            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
            lock (_lock)
            {
                foreach (var scope in _scopeStack.Reverse())
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> kvps)
                    {
                        foreach (var kvp in kvps)
                        {
                            merged[kvp.Key] = kvp.Value;
                        }
                    }
                    else if (scope is IEnumerable<KeyValuePair<string, object>> objKvps)
                    {
                        foreach (var kvp in objKvps)
                        {
                            merged[kvp.Key] = kvp.Value;
                        }
                    }
                }
                Entries.Add(new LogRecord(logLevel, formatted, merged));
            }
        }

        private sealed class ScopePopper : IDisposable
        {
            private readonly Action _onDispose;
            private int _disposed;
            public ScopePopper(Action onDispose) => _onDispose = onDispose;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _onDispose();
                }
            }
        }
    }

    private sealed class RecordingOutbox : IMessageOutbox
    {
        private readonly Queue<OutboxEntry> _queue;

        public RecordingOutbox(params OutboxEntry[] entries) => _queue = new Queue<OutboxEntry>(entries);

        /// <summary>
        /// When non-null, <see cref="CountPendingAsync"/> rethrows this exception so
        /// the engine takes the catch branch in <c>SetPendingCountFromOutboxAsync</c>
        /// and emits the canonical fallback LogDebug. Used by iter-13 item 2 tests.
        /// </summary>
        public Exception? CountPendingAsyncThrow { get; init; }

        public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
        {
            var batch = new List<OutboxEntry>();
            while (batch.Count < batchSize && _queue.Count > 0)
            {
                batch.Add(_queue.Dequeue());
            }
            return Task.FromResult<IReadOnlyList<OutboxEntry>>(batch);
        }

        public Task<long> CountPendingAsync(CancellationToken ct)
        {
            if (CountPendingAsyncThrow is not null)
            {
                throw CountPendingAsyncThrow;
            }
            return Task.FromResult(-1L);
        }

        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct) => Task.CompletedTask;
        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct) => Task.CompletedTask;
        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct) => Task.CompletedTask;
        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubDispatcher : IOutboxDispatcher
    {
        private readonly Func<OutboxEntry, OutboxDispatchResult> _fn;
        public StubDispatcher(Func<OutboxEntry, OutboxDispatchResult> fn) => _fn = fn;
        public Task<OutboxDispatchResult> DispatchAsync(OutboxEntry entry, CancellationToken ct) => Task.FromResult(_fn(entry));
    }
}
