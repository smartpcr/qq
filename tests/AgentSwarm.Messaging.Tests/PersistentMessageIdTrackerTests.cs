using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.3 — durability of the (chatId, telegramMessageId) →
/// correlationId mapping. Pins the iter-2 evaluator's item 2 ("loses
/// the Telegram message-id-to-correlation mapping on restart"). Uses
/// SQLite in-memory (shared connection so the schema survives across
/// the multiple short-lived scopes the tracker opens).
/// </summary>
public class PersistentMessageIdTrackerTests
{
    private static (ServiceProvider Provider, SqliteConnection Connection) BuildProvider()
    {
        // SQLite in-memory uses a per-connection isolated database, so
        // we pre-open one shared SqliteConnection that the DbContext
        // pool reuses for every scope. This lets the schema created in
        // one scope be visible to subsequent scopes.
        //
        // Iter-5 fix (iter-4 evaluator item 4): use Database.Migrate()
        // here for production parity with DatabaseInitializer (which
        // applies migrations via MigrateAsync). Migrate() exercises
        // the actual migration pipeline including the
        // OutboundMessageIdMapping composite-key migration that the
        // PersistentMessageIdTracker depends on, so a regression in
        // that migration would surface here. EnsureCreated() would
        // bypass migrations entirely and silently mask such a bug.
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MessagingDbContext>(opts =>
            opts.UseSqlite(connection));
        services.AddSingleton<IMessageIdTracker, PersistentMessageIdTracker>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLoggerWrapper<>));

        var provider = services.BuildServiceProvider();

        // Initialize schema once via the real migration pipeline.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            db.Database.Migrate();
        }

        return (provider, connection);
    }

    [Fact]
    public async Task TrackAsync_PersistsRow_ThatTryGetCorrelationIdAsyncCanResolve()
    {
        var (provider, connection) = BuildProvider();
        try
        {
            var tracker = provider.GetRequiredService<IMessageIdTracker>();

            await tracker.TrackAsync(123L, 999_001L, "corr-X", CancellationToken.None);
            var resolved = await tracker.TryGetCorrelationIdAsync(123L, 999_001L, CancellationToken.None);

            resolved.Should().Be("corr-X");
        }
        finally
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    [Fact]
    public async Task TrackAsync_DoesNotCollideAcrossChats_ForSameTelegramMessageId()
    {
        var (provider, connection) = BuildProvider();
        try
        {
            var tracker = provider.GetRequiredService<IMessageIdTracker>();
            const long sharedMessageId = 555_555L;

            await tracker.TrackAsync(1001L, sharedMessageId, "corr-chat-1", CancellationToken.None);
            await tracker.TrackAsync(2002L, sharedMessageId, "corr-chat-2", CancellationToken.None);

            var fromChat1 = await tracker.TryGetCorrelationIdAsync(1001L, sharedMessageId, CancellationToken.None);
            var fromChat2 = await tracker.TryGetCorrelationIdAsync(2002L, sharedMessageId, CancellationToken.None);
            fromChat1.Should().Be("corr-chat-1");
            fromChat2.Should().Be("corr-chat-2",
                "the (chatId, telegramMessageId) composite primary key prevents the second insert from overwriting the first");
        }
        finally
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    [Fact]
    public async Task TrackAsync_SurvivesScopeBoundary_DemonstratesDurability()
    {
        var (provider, connection) = BuildProvider();
        try
        {
            // First scope: write the mapping.
            var trackerA = provider.GetRequiredService<IMessageIdTracker>();
            await trackerA.TrackAsync(42L, 7_000_001L, "corr-durable", CancellationToken.None);

            // Second scope (the tracker is a singleton, but its
            // TrackAsync/TryGetCorrelationIdAsync each open and dispose
            // their own DbContext scope per call — proving the data
            // lives in the database, not in any per-scope buffer).
            var trackerB = provider.GetRequiredService<IMessageIdTracker>();
            var resolved = await trackerB.TryGetCorrelationIdAsync(42L, 7_000_001L, CancellationToken.None);

            resolved.Should().Be("corr-durable",
                "the row written by an earlier scope must be readable by a later scope — this is what the iter-1 in-memory tracker could not do");
        }
        finally
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    [Fact]
    public async Task TrackAsync_RewritingSameComposite_Upserts_DoesNotThrowUniqueViolation()
    {
        var (provider, connection) = BuildProvider();
        try
        {
            var tracker = provider.GetRequiredService<IMessageIdTracker>();

            await tracker.TrackAsync(1L, 2L, "corr-1", CancellationToken.None);
            await tracker.TrackAsync(1L, 2L, "corr-2", CancellationToken.None);

            var resolved = await tracker.TryGetCorrelationIdAsync(1L, 2L, CancellationToken.None);
            resolved.Should().Be("corr-2",
                "rewriting the same (chatId, telegramMessageId) pair must perform an upsert — Telegram does retry-deduplicate within a chat, so the same composite key can legitimately be re-recorded");
        }
        finally
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    // ============================================================
    // Iter-3 item 1: the IMessageIdTracker.TrackAsync contract states
    // that implementations MUST NOT propagate persistence failures.
    // The PersistentMessageIdTracker honours the contract by performing
    // up to MaxAttempts attempts with exponential backoff and, on
    // persistent failure, logging an Error-level event and suppressing
    // the exception. The two tests below pin both halves of that
    // contract on the production implementation.
    //
    // We use a FlakyScopeFactory that throws from CreateScope() on the
    // first N calls — this triggers the catch-and-retry path in
    // TrackAsync. A ZeroDelayTimeProvider collapses the backoff sleeps
    // to no-ops so the tests run in milliseconds rather than seconds.
    // ============================================================

    [Fact]
    public async Task TrackAsync_FirstAttemptFails_SecondAttemptSucceeds_PersistsRow()
    {
        var (provider, connection) = BuildProvider();
        try
        {
            var inner = provider.GetRequiredService<IServiceScopeFactory>();
            var flaky = new FlakyScopeFactory(inner, failuresToInject: 1);
            var capturingLogger = new ListLogger<PersistentMessageIdTracker>();
            var tracker = new PersistentMessageIdTracker(
                flaky,
                capturingLogger,
                new ZeroDelayTimeProvider());

            await tracker.TrackAsync(700L, 800L, "corr-recovered", CancellationToken.None);

            flaky.FailureCount.Should().Be(1, "exactly one transient failure was injected");
            flaky.SuccessCount.Should().Be(1, "the second attempt succeeded and reached the inner scope");

            // Verify the row actually landed in the database — i.e. the
            // recovery wasn't a silent suppression that gave up after
            // the first attempt.
            var verifierTracker = provider.GetRequiredService<IMessageIdTracker>();
            var resolved = await verifierTracker.TryGetCorrelationIdAsync(700L, 800L, CancellationToken.None);
            resolved.Should().Be("corr-recovered",
                "the row must be readable after recovery — proving the second attempt persisted, not just returned");

            capturingLogger.Entries.Should().Contain(e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                e.Message.Contains("attempt"),
                "a warning is logged on each retry so operators can correlate transient DB hiccups");
            capturingLogger.Entries.Should().NotContain(e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Error,
                "no error is logged when recovery succeeds within MaxAttempts");
        }
        finally
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    [Fact]
    public async Task TrackAsync_AllAttemptsFail_DoesNotThrow_LogsError()
    {
        var (provider, connection) = BuildProvider();
        try
        {
            var inner = provider.GetRequiredService<IServiceScopeFactory>();
            // Inject more failures than MaxAttempts to ensure every
            // attempt fails; the tracker must give up via log+suppress
            // rather than propagate.
            var flaky = new FlakyScopeFactory(inner, failuresToInject: PersistentMessageIdTracker.MaxAttempts + 5);
            var capturingLogger = new ListLogger<PersistentMessageIdTracker>();
            var tracker = new PersistentMessageIdTracker(
                flaky,
                capturingLogger,
                new ZeroDelayTimeProvider());

            // The contract: this call must NOT throw. If it does, the
            // OutboundQueueProcessor would re-send a Telegram-delivered
            // message and the operator would see a duplicate.
            var act = async () => await tracker.TrackAsync(900L, 1000L, "corr-doomed", CancellationToken.None);
            await act.Should().NotThrowAsync(
                "the IMessageIdTracker contract requires implementations to suppress persistence failures — propagation would cause the upstream OutboundQueueProcessor to retry and produce a duplicate operator-visible message in the chat");

            flaky.FailureCount.Should().Be(PersistentMessageIdTracker.MaxAttempts,
                $"the tracker tries exactly MaxAttempts ({PersistentMessageIdTracker.MaxAttempts}) times before giving up");
            flaky.SuccessCount.Should().Be(0, "no attempt reached the inner scope");

            capturingLogger.Entries.Should().Contain(e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Error &&
                e.Message.Contains("exhausted") &&
                e.Message.Contains("OutboundMessage"),
                "the Error-level log must reference both the exhaustion and the canonical durable record (the OutboundMessage row) so operators know where to recover from");
        }
        finally
        {
            provider.Dispose();
            connection.Dispose();
        }
    }

    // Scope factory that throws from CreateScope() on its first
    // <c>failuresToInject</c> calls, then delegates to the inner
    // factory. Used to simulate transient DB-connection failures (a
    // closed pool, a DNS hiccup, a Sqlite "database is locked" race,
    // etc.) without hand-rolling a flaky DbContext.
    private sealed class FlakyScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private int _failuresRemaining;
        public int FailureCount { get; private set; }
        public int SuccessCount { get; private set; }

        public FlakyScopeFactory(IServiceScopeFactory inner, int failuresToInject)
        {
            _inner = inner;
            _failuresRemaining = failuresToInject;
        }

        public IServiceScope CreateScope()
        {
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                FailureCount++;
                throw new InvalidOperationException("simulated transient DB outage (FlakyScopeFactory)");
            }
            SuccessCount++;
            return _inner.CreateScope();
        }
    }

    // TimeProvider whose timers fire immediately so Task.Delay returns
    // synchronously. This collapses the tracker's backoff sleeps to
    // effective no-ops, keeping the tests sub-second even when
    // MaxAttempts is exhausted.
    private sealed class ZeroDelayTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new ImmediateTimer(callback, state);

        private sealed class ImmediateTimer : ITimer
        {
            public ImmediateTimer(TimerCallback callback, object? state)
            {
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // Captures structured log entries for inspection. Keeps the
    // (Level, Message) pair so tests can assert on the warning vs
    // error split without depending on a logging framework mock.
    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // Open-generic logger that returns NullLogger<T> for any T —
    // satisfies the constructor's ILogger<PersistentMessageIdTracker>
    // dependency without a separate registration per concrete logger.
    private sealed class NullLoggerWrapper<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
