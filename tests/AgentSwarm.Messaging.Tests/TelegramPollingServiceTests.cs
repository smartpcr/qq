using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Polling;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.5 — locks the behavior of <see cref="TelegramPollingService"/>:
/// the long-poll loop reads updates from the poller, maps each via
/// <see cref="TelegramUpdateMapper"/>, hands the result to
/// <see cref="ITelegramUpdatePipeline.ProcessAsync"/>, advances offset only
/// after pipeline completion, and exits cleanly on cancellation logging
/// the final offset.
/// </summary>
public class TelegramPollingServiceTests
{
    [Fact]
    public async Task ExecuteAsync_DoesNothing_WhenUsePollingFalse()
    {
        var poller = new FakePoller();
        var pipeline = new FakePipeline();
        using var service = CreateService(poller, pipeline, usePolling: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        poller.GetUpdatesCalls.Should().Be(0,
            "TelegramPollingService must be a no-op when UsePolling=false");
        pipeline.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_PollsAndForwardsUpdatesToPipeline()
    {
        var update1 = MakeMessageUpdate(id: 10, "/status");
        var update2 = MakeMessageUpdate(id: 11, "hello");

        var poller = new FakePoller();
        poller.SeedBatch(new[] { update1, update2 });
        // Subsequent calls return empty arrays; the loop continues until
        // cancellation.
        var pipeline = new FakePipeline();

        using var service = CreateService(poller, pipeline, usePolling: true);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await poller.WaitForUpdatesCallsAsync(2, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        pipeline.Calls.Should().HaveCountGreaterThanOrEqualTo(2);
        var receivedIds = pipeline.Calls.Take(2).Select(c => c.EventId).ToArray();
        receivedIds.Should().BeEquivalentTo(new[] { "tg-update-10", "tg-update-11" });
        pipeline.Calls[0].EventType.Should().Be(EventType.Command);
        pipeline.Calls[1].EventType.Should().Be(EventType.TextReply);
    }

    [Fact]
    public async Task ExecuteAsync_AdvancesOffsetPastProcessedUpdates()
    {
        var update1 = MakeMessageUpdate(id: 10, "/status");
        var update2 = MakeMessageUpdate(id: 25, "ack");

        var poller = new FakePoller();
        poller.SeedBatch(new[] { update1, update2 });
        var pipeline = new FakePipeline();

        using var service = CreateService(poller, pipeline, usePolling: true);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await poller.WaitForUpdatesCallsAsync(2, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        poller.OffsetsSeen.Should().Contain((int?)null, "first poll passes offset=null");
        poller.OffsetsSeen.Should().Contain(26,
            "after processing update.Id=25 the next poll passes offset=26");
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotAdvancePastFailedUpdate()
    {
        var update1 = MakeMessageUpdate(id: 50, "/status");
        var update2 = MakeMessageUpdate(id: 51, "/agents");
        var update3 = MakeMessageUpdate(id: 52, "/start");

        var poller = new FakePoller();
        poller.SeedBatch(new[] { update1, update2, update3 });
        var pipeline = new FakePipeline
        {
            FailOnEventId = "tg-update-51",
        };

        using var service = CreateService(poller, pipeline, usePolling: true);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await poller.WaitForUpdatesCallsAsync(2, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // After processing 50 the offset advances to 51; processing 51
        // throws, so the batch breaks and 52 is NOT processed in this
        // batch. The next poll should re-fetch from 51.
        poller.OffsetsSeen.Should().Contain(51,
            "the next poll after a failed update.Id=51 must re-request starting from 51 so the failed update is redelivered");
        poller.OffsetsSeen.Should().NotContain(53,
            "the batch must NOT advance past the failed update.Id=51 to 53; that would ack updates 51 and 52 and lose them");
    }

    [Fact]
    public async Task ExecuteAsync_DeletesStaleWebhookBeforePolling()
    {
        var poller = new FakePoller();
        var pipeline = new FakePipeline();

        using var service = CreateService(poller, pipeline, usePolling: true);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await poller.WaitForUpdatesCallsAsync(1, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        poller.DeleteWebhookCalls.Should().BeGreaterThanOrEqualTo(1,
            "polling service must clear any stale webhook before issuing getUpdates to avoid the 409 conflict");
        poller.DeleteWebhookDropPendingValues.Should().Contain(false,
            "DeleteWebhook must NOT drop pending updates (would silently discard operator input)");
    }

    [Fact]
    public async Task ExecuteAsync_LogsFinalOffset_OnCancellation()
    {
        var update = MakeMessageUpdate(id: 77, "/status");
        var poller = new FakePoller();
        poller.SeedBatch(new[] { update });
        var pipeline = new FakePipeline();
        var logger = new CapturingLogger<TelegramPollingService>();

        using var service = CreateService(poller, pipeline, usePolling: true, logger: logger);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await poller.WaitForUpdatesCallsAsync(2, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        logger.Logs.Should().Contain(e =>
            e.Contains("Telegram polling service stopped") && e.Contains("FinalOffset"),
            "the final offset must be logged on graceful shutdown so operators can resume cleanly");
    }

    [Fact]
    public async Task ExecuteAsync_LogsFinalOffset_WhenCancelledDuringWebhookClear()
    {
        // Regression test for the early-cancellation path: cancellation
        // fires WHILE the startup DeleteWebhookAsync call is still in
        // flight. The graceful-shutdown contract (Stage 2.5) requires the
        // final offset to be logged on every shutdown path, including
        // before the first long-poll has executed.
        var poller = new FakePoller
        {
            BlockDeleteWebhookUntilCancelled = true,
        };
        var pipeline = new FakePipeline();
        var logger = new CapturingLogger<TelegramPollingService>();

        using var service = CreateService(poller, pipeline, usePolling: true, logger: logger);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Wait until the service has entered DeleteWebhookAsync, then
        // cancel mid-flight.
        await poller.WaitForDeleteWebhookEnteredAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        poller.GetUpdatesCalls.Should().Be(0,
            "cancellation fired before the first long-poll; getUpdates must not have been called");

        logger.Logs.Should().Contain(e =>
            e.Contains("Telegram polling service stopped") && e.Contains("FinalOffset=(none)"),
            "even when cancellation fires during the startup DeleteWebhookAsync call, the final-offset log line must still run; offset is (none) because no updates were processed");
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesPolling_AfterTransientPollerFailure()
    {
        var poller = new FakePoller();
        poller.ThrowOnFirstPoll = new InvalidOperationException("transient");
        poller.SeedBatch(new[] { MakeMessageUpdate(id: 1, "/status") });
        var pipeline = new FakePipeline();

        using var service = CreateService(poller, pipeline, usePolling: true);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        // Wait long enough for the backoff (~5s) + a successful poll
        await poller.WaitForUpdatesCallsAsync(2, TimeSpan.FromSeconds(10));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        pipeline.Calls.Should().NotBeEmpty(
            "a transient poller failure must not terminate the loop; subsequent polls must succeed and reach the pipeline");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyBatch_DoesNotInvokePipeline()
    {
        var poller = new FakePoller();
        // No batch seeded — every poll returns empty.
        var pipeline = new FakePipeline();

        using var service = CreateService(poller, pipeline, usePolling: true);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await poller.WaitForUpdatesCallsAsync(2, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        pipeline.Calls.Should().BeEmpty(
            "an empty batch (long-poll timed out without new traffic) must not invoke the pipeline");
        poller.OffsetsSeen.Should().OnlyContain(o => o == null,
            "offset must remain null while no updates have been processed");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static TelegramPollingService CreateService(
        ITelegramUpdatePoller poller,
        ITelegramUpdatePipeline pipeline,
        bool usePolling,
        CapturingLogger<TelegramPollingService>? logger = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TelegramOptions
        {
            BotToken = "1234:test_token",
            UsePolling = usePolling,
            PollingTimeoutSeconds = 1, // keep tests fast
        });
        return new TelegramPollingService(
            poller,
            pipeline,
            options,
            (Microsoft.Extensions.Logging.ILogger<TelegramPollingService>?)logger
                ?? NullLogger<TelegramPollingService>.Instance)
        {
            // Shorten the post-failure backoff so the suite is fast; the
            // production default (5s) is too long for unit tests.
            TransientBackoff = TimeSpan.FromMilliseconds(25),
        };
    }

    private static Update MakeMessageUpdate(int id, string text)
    {
        return new Update
        {
            Id = id,
            Message = new Message
            {
                Id = id,
                Text = text,
                Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                From = new User { Id = 1000 + id, IsBot = false, FirstName = "Op" },
                Chat = new Chat { Id = 2000 + id, Type = ChatType.Private },
            },
        };
    }

    private sealed class FakePoller : ITelegramUpdatePoller
    {
        private readonly object _gate = new();
        private readonly Queue<Update[]> _batches = new();
        private int _getUpdatesCalls;
        private readonly TaskCompletionSource<bool> _firstCallSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? ThrowOnFirstPoll { get; set; }
        public List<int?> OffsetsSeen { get; } = new();
        public int GetUpdatesCalls => Volatile.Read(ref _getUpdatesCalls);
        public int DeleteWebhookCalls;
        public List<bool> DeleteWebhookDropPendingValues { get; } = new();

        public void SeedBatch(Update[] batch)
        {
            lock (_gate)
            {
                _batches.Enqueue(batch);
            }
        }

        public Task DeleteWebhookAsync(bool dropPendingUpdates, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DeleteWebhookCalls);
            lock (_gate)
            {
                DeleteWebhookDropPendingValues.Add(dropPendingUpdates);
            }
            if (BlockDeleteWebhookUntilCancelled)
            {
                // Signal that we've entered the call so the test can race
                // cancellation against the await. The continuation throws
                // OperationCanceledException when the token fires, which is
                // exactly how the real Telegram.Bot HttpClient pipeline
                // surfaces in-flight cancellation.
                _deleteWebhookEnteredSignal.TrySetResult(true);
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// When true, DeleteWebhookAsync awaits an infinite Task.Delay bound
        /// to the cancellation token. The polling service's startup
        /// webhook-clear call will not return until cancellation fires —
        /// this models a real cancellation-during-HTTP-request scenario.
        /// </summary>
        public bool BlockDeleteWebhookUntilCancelled { get; set; }

        private readonly TaskCompletionSource<bool> _deleteWebhookEnteredSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForDeleteWebhookEnteredAsync(TimeSpan timeout) =>
            Task.WhenAny(_deleteWebhookEnteredSignal.Task, Task.Delay(timeout));

        public async Task<Update[]> GetUpdatesAsync(int? offset, int timeout, CancellationToken cancellationToken)
        {
            Update[] batch;
            lock (_gate)
            {
                OffsetsSeen.Add(offset);
                Interlocked.Increment(ref _getUpdatesCalls);
                _firstCallSignal.TrySetResult(true);

                if (ThrowOnFirstPoll is not null && _getUpdatesCalls == 1)
                {
                    var ex = ThrowOnFirstPoll;
                    ThrowOnFirstPoll = null;
                    throw ex;
                }

                batch = _batches.Count > 0 ? _batches.Dequeue() : Array.Empty<Update>();
            }

            if (batch.Length == 0)
            {
                // Simulate a short long-poll wait so the loop yields and
                // doesn't spin tightly. The cancellation token aborts.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }

            return batch;
        }

        public async Task WaitForUpdatesCallsAsync(int target, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (GetUpdatesCalls < target && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }

    private sealed class FakePipeline : ITelegramUpdatePipeline
    {
        public List<MessengerEvent> Calls { get; } = new();
        public string? FailOnEventId { get; set; }

        public Task<PipelineResult> ProcessAsync(MessengerEvent messengerEvent, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add(messengerEvent);
            }

            if (FailOnEventId is not null && messengerEvent.EventId == FailOnEventId)
            {
                throw new InvalidOperationException("simulated pipeline failure");
            }

            return Task.FromResult(new PipelineResult
            {
                Handled = true,
                CorrelationId = messengerEvent.CorrelationId,
            });
        }
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public ConcurrentBag<string> Logs { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
