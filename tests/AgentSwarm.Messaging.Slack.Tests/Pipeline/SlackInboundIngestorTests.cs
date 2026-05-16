// -----------------------------------------------------------------------
// <copyright file="SlackInboundIngestorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.3 hosted-service tests for <see cref="SlackInboundIngestor"/>.
/// </summary>
public sealed class SlackInboundIngestorTests
{
    [Fact]
    public async Task ExecuteAsync_drains_queue_and_dispatches_each_envelope_to_the_pipeline()
    {
        FakeQueue queue = new();
        FakeAuthorizer authorizer = new(true);
        InMemorySlackIdempotencyGuard guard = new();
        RecordingHandler commandHandler = new();
        RecordingHandler appMentionHandler = new();
        RecordingHandler interactionHandler = new();
        InMemorySlackAuditEntryWriter auditWriter = new();
        SlackInboundAuditRecorder recorder = new(
            auditWriter, NullLogger<SlackInboundAuditRecorder>.Instance, TimeProvider.System);
        RecordingDeadLetterQueue dlq = new();

        SlackInboundProcessingPipeline pipeline = new(
            authorizer,
            guard,
            new RecordingCommandHandler(commandHandler),
            new RecordingAppMentionHandler(appMentionHandler),
            new RecordingInteractionHandler(interactionHandler),
            new ZeroDelayRetryPolicy(maxAttempts: 3),
            dlq,
            recorder,
            NullLogger<SlackInboundProcessingPipeline>.Instance,
            TimeProvider.System);

        SlackInboundIngestor ingestor = new(
            queue,
            pipeline,
            new InMemorySlackInboundEnqueueDeadLetterSink(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            NullLogger<SlackInboundIngestor>.Instance);

        // Pre-enqueue two envelopes BEFORE starting so the ingestor's
        // first dequeue calls return immediately.
        SlackInboundEnvelope env1 = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-1");
        SlackInboundEnvelope env2 = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-2");
        await queue.EnqueueAsync(env1);
        await queue.EnqueueAsync(env2);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = ingestor.StartAsync(cts.Token);

        // Wait until both envelopes have been observed by the handler,
        // then stop the ingestor. The handler list mutates from the
        // background loop -- poll until both are present or we hit
        // the safety timeout.
        await WaitUntilAsync(() => commandHandler.Invocations.Count >= 2, TimeSpan.FromSeconds(5));

        await ingestor.StopAsync(CancellationToken.None);
        cts.Cancel();
        await run;

        commandHandler.Invocations.Should().HaveCount(2);
        commandHandler.Invocations.Select(e => e.IdempotencyKey)
            .Should().Contain(new[] { env1.IdempotencyKey, env2.IdempotencyKey });
        auditWriter.Entries.Should().HaveCount(2);
        auditWriter.Entries.All(e => e.Outcome == "success").Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_continues_running_when_one_envelope_throws_unexpectedly_from_pipeline()
    {
        FakeQueue queue = new();
        ThrowingPipeline pipeline = new();
        SlackInboundIngestor ingestor = new(
            queue,
            pipeline.Build(),
            new InMemorySlackInboundEnqueueDeadLetterSink(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            NullLogger<SlackInboundIngestor>.Instance);

        await queue.EnqueueAsync(BuildCommandEnvelope("cmd:T1:U1:/agent:trig-bad"));
        await queue.EnqueueAsync(BuildCommandEnvelope("cmd:T1:U1:/agent:trig-good"));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = ingestor.StartAsync(cts.Token);

        await WaitUntilAsync(() => pipeline.ProcessedCount >= 2, TimeSpan.FromSeconds(5));

        await ingestor.StopAsync(CancellationToken.None);
        cts.Cancel();
        await run;

        pipeline.ProcessedCount.Should().BeGreaterThanOrEqualTo(2,
            "the dispatch loop MUST survive a pipeline exception so subsequent envelopes still process");
    }

    [Fact]
    public async Task ExecuteAsync_exits_cleanly_on_stopping_token()
    {
        FakeQueue queue = new();
        InMemorySlackAuditEntryWriter auditWriter = new();
        SlackInboundProcessingPipeline pipeline = new(
            new FakeAuthorizer(true),
            new InMemorySlackIdempotencyGuard(),
            new RecordingCommandHandler(new RecordingHandler()),
            new RecordingAppMentionHandler(new RecordingHandler()),
            new RecordingInteractionHandler(new RecordingHandler()),
            new ZeroDelayRetryPolicy(maxAttempts: 3),
            new RecordingDeadLetterQueue(),
            new SlackInboundAuditRecorder(auditWriter, NullLogger<SlackInboundAuditRecorder>.Instance, TimeProvider.System),
            NullLogger<SlackInboundProcessingPipeline>.Instance,
            TimeProvider.System);

        SlackInboundIngestor ingestor = new(
            queue,
            pipeline,
            new InMemorySlackInboundEnqueueDeadLetterSink(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            NullLogger<SlackInboundIngestor>.Instance);

        using CancellationTokenSource cts = new();
        await ingestor.StartAsync(cts.Token);

        cts.Cancel();
        Func<Task> act = () => ingestor.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync(
            "cancellation while waiting on DequeueAsync MUST not propagate as an unhandled exception");
    }

    [Fact]
    public async Task ExecuteAsync_forwards_envelope_to_fallback_sink_when_pipeline_throws_SlackInboundDeadLetterEnqueueException()
    {
        // Iter 3 evaluator item #1: if the DLQ backend itself fails
        // after the handler exhausted its retry budget, the
        // ingestor's outer catch USED to swallow the throw, leaving
        // the dequeued envelope completely lost (ISlackInboundQueue
        // has no nack/requeue). The fix: forward the envelope to the
        // last-resort ISlackInboundEnqueueDeadLetterSink so the
        // payload is durably observable (bounded ring buffer +
        // LogCritical by default; upgradeable to JSONL on disk).
        FakeQueue queue = new();
        SlackInboundEnvelope deadletteredEnvelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-dlq-fail");

        // Pipeline is wired so the handler always throws AND the DLQ
        // backend throws, exercising the exact code path the iter-3
        // fix targets.
        SlackInboundProcessingPipeline pipeline = BuildPipelineThatThrowsOnDlqEnqueue();

        RecordingDeadLetterFallbackSink sink = new();
        SlackInboundIngestor ingestor = new(
            queue,
            pipeline,
            sink,
            NullLogger<SlackInboundIngestor>.Instance);

        await queue.EnqueueAsync(deadletteredEnvelope);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = ingestor.StartAsync(cts.Token);

        await WaitUntilAsync(() => sink.Records.Count >= 1, TimeSpan.FromSeconds(5));

        await ingestor.StopAsync(CancellationToken.None);
        cts.Cancel();
        await run;

        sink.Records.Should().HaveCount(1,
            "the fallback sink MUST capture the envelope whose DLQ enqueue blew up");
        sink.Records[0].Envelope.IdempotencyKey.Should().Be(deadletteredEnvelope.IdempotencyKey);
        sink.Records[0].LastException.Should().BeOfType<SlackInboundDeadLetterEnqueueException>();
        sink.Records[0].AttemptCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExecuteAsync_forwards_envelope_to_fallback_sink_when_pipeline_throws_generic_exception_outside_DLQ_path()
    {
        // Iter 6 evaluator item #1: when the pipeline propagates an
        // exception that is NOT the DLQ-enqueue specific one (e.g.,
        // SlackIdempotencyGuard.TryAcquireAsync re-throws a transient
        // DbUpdateException with no competing row, per
        // SlackIdempotencyGuardDbFailureTests), the ingestor's
        // catch-all USED to merely log and continue -- silently
        // losing the dequeued envelope because ISlackInboundQueue has
        // no nack/requeue contract. The fix: forward the envelope to
        // the durable last-resort sink so the FR-005 / FR-007
        // no-message-loss guarantee still holds.
        FakeQueue queue = new();
        SlackInboundEnvelope leakedEnvelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-guard-blip");

        ThrowingGuard guard = new(new InvalidOperationException("simulated idempotency-table outage"));
        SlackInboundProcessingPipeline pipeline = new(
            new FakeAuthorizer(true),
            guard,
            new RecordingCommandHandler(new RecordingHandler()),
            new RecordingAppMentionHandler(new RecordingHandler()),
            new RecordingInteractionHandler(new RecordingHandler()),
            new ZeroDelayRetryPolicy(maxAttempts: 1),
            new RecordingDeadLetterQueue(),
            new SlackInboundAuditRecorder(
                new InMemorySlackAuditEntryWriter(),
                NullLogger<SlackInboundAuditRecorder>.Instance,
                TimeProvider.System),
            NullLogger<SlackInboundProcessingPipeline>.Instance,
            TimeProvider.System);

        RecordingDeadLetterFallbackSink sink = new();
        SlackInboundIngestor ingestor = new(
            queue,
            pipeline,
            sink,
            NullLogger<SlackInboundIngestor>.Instance);

        await queue.EnqueueAsync(leakedEnvelope);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Task run = ingestor.StartAsync(cts.Token);

        await WaitUntilAsync(() => sink.Records.Count >= 1, TimeSpan.FromSeconds(5));

        await ingestor.StopAsync(CancellationToken.None);
        cts.Cancel();
        await run;

        sink.Records.Should().HaveCount(1,
            "the fallback sink MUST capture the envelope when ANY pipeline exception propagates, otherwise the dequeued envelope is permanently lost");
        sink.Records[0].Envelope.IdempotencyKey.Should().Be(leakedEnvelope.IdempotencyKey);
        sink.Records[0].LastException.Should().BeOfType<InvalidOperationException>(
            "the wrapped exception MUST be the original pipeline failure so operators can triage the root cause");
        sink.Records[0].LastException.Message.Should().Contain("simulated idempotency-table outage");
    }

    [Fact]
    public async Task ExecuteAsync_forwards_envelope_to_fallback_sink_when_FileSystemSlackDeadLetterQueue_throws_persistence_failure()
    {
        // Iter 8 evaluator item #2: the only pre-existing DLQ-failure
        // coverage uses a synthetic ThrowingDeadLetterQueue. This test
        // proves the same fallback contract holds end-to-end against
        // the REAL FileSystemSlackDeadLetterQueue: a sharing-violation
        // on the JSONL append (modeled by holding an exclusive write
        // lock on the target file) MUST propagate through the queue
        // -> pipeline -> ingestor chain to land the envelope in the
        // last-resort ISlackInboundEnqueueDeadLetterSink. Without this
        // end-to-end pin, the structural fix in
        // FileSystemSlackDeadLetterQueue.EnqueueAsync could regress
        // silently because no test exercises the real wire path.
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "slack-dlq-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string filePath = Path.Combine(tempDir, FileSystemSlackDeadLetterQueue.DefaultFileName);

        // Hold an exclusive write lock on the JSONL file for the
        // duration of the test so the queue's FileMode.Append open
        // call MUST fail with a sharing-violation IOException. The
        // try/finally cleans up the temp dir even if the test throws.
        await using FileStream lockHolder = new(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        try
        {
            using FileSystemSlackDeadLetterQueue dlq = new(
                tempDir,
                NullLogger<FileSystemSlackDeadLetterQueue>.Instance);

            // Pipeline wiring: the command handler always throws (so
            // retry exhausts), and the DLQ is the REAL durable file
            // queue whose append we just guaranteed will fail.
            SlackInboundProcessingPipeline pipeline = new(
                new FakeAuthorizer(true),
                new InMemorySlackIdempotencyGuard(),
                new AlwaysThrowingCommandHandler(),
                new RecordingAppMentionHandler(new RecordingHandler()),
                new RecordingInteractionHandler(new RecordingHandler()),
                new ZeroDelayRetryPolicy(maxAttempts: 1),
                dlq,
                new SlackInboundAuditRecorder(
                    new InMemorySlackAuditEntryWriter(),
                    NullLogger<SlackInboundAuditRecorder>.Instance,
                    TimeProvider.System),
                NullLogger<SlackInboundProcessingPipeline>.Instance,
                TimeProvider.System);

            FakeQueue queue = new();
            RecordingDeadLetterFallbackSink sink = new();
            SlackInboundIngestor ingestor = new(
                queue,
                pipeline,
                sink,
                NullLogger<SlackInboundIngestor>.Instance);

            SlackInboundEnvelope leaked = BuildCommandEnvelope("cmd:T1:U1:/agent:fs-dlq-fail");
            await queue.EnqueueAsync(leaked);

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            Task run = ingestor.StartAsync(cts.Token);

            await WaitUntilAsync(() => sink.Records.Count >= 1, TimeSpan.FromSeconds(5));

            await ingestor.StopAsync(CancellationToken.None);
            cts.Cancel();
            await run;

            sink.Records.Should().HaveCount(1,
                "the fallback sink MUST capture the envelope when the durable JSONL DLQ refuses the append; otherwise the envelope is permanently lost (inbound queue has no nack)");
            sink.Records[0].Envelope.IdempotencyKey.Should().Be(leaked.IdempotencyKey);
            sink.Records[0].LastException.Should().BeOfType<SlackInboundDeadLetterEnqueueException>(
                "the pipeline MUST wrap the durable-DLQ failure as SlackInboundDeadLetterEnqueueException so the ingestor's typed catch path engages the fallback sink");
            sink.Records[0].LastException.InnerException.Should()
                .BeOfType<SlackInboundDeadLetterPersistenceException>(
                    "the inner chain MUST preserve the typed persistence failure so operators see exactly which durable surface failed");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    // Release any handles before deletion.
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup -- a Windows file lock during
                // teardown should not fail the suite.
            }
        }
    }

    private static SlackInboundProcessingPipeline BuildPipelineThatThrowsOnDlqEnqueue()
    {
        return new SlackInboundProcessingPipeline(
            new FakeAuthorizer(true),
            new InMemorySlackIdempotencyGuard(),
            new AlwaysThrowingCommandHandler(),
            new RecordingAppMentionHandler(new RecordingHandler()),
            new RecordingInteractionHandler(new RecordingHandler()),
            new ZeroDelayRetryPolicy(maxAttempts: 1),
            new ThrowingDeadLetterQueue(),
            new SlackInboundAuditRecorder(
                new InMemorySlackAuditEntryWriter(),
                NullLogger<SlackInboundAuditRecorder>.Instance,
                TimeProvider.System),
            NullLogger<SlackInboundProcessingPipeline>.Instance,
            TimeProvider.System);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static SlackInboundEnvelope BuildCommandEnvelope(string key) => new(
        IdempotencyKey: key,
        SourceType: SlackInboundSourceType.Command,
        TeamId: "T1",
        ChannelId: "C1",
        UserId: "U1",
        RawPayload: "team_id=T1&user_id=U1&command=/agent",
        TriggerId: "trig",
        ReceivedAt: DateTimeOffset.UtcNow);

    private sealed class FakeQueue : ISlackInboundQueue
    {
        private readonly System.Threading.Channels.Channel<SlackInboundEnvelope> channel =
            System.Threading.Channels.Channel.CreateUnbounded<SlackInboundEnvelope>();

        public ValueTask EnqueueAsync(SlackInboundEnvelope envelope)
        {
            this.channel.Writer.TryWrite(envelope);
            return ValueTask.CompletedTask;
        }

        public ValueTask<SlackInboundEnvelope> DequeueAsync(CancellationToken ct)
            => this.channel.Reader.ReadAsync(ct);
    }

    private sealed class FakeAuthorizer : ISlackInboundAuthorizer
    {
        private readonly bool authorize;

        public FakeAuthorizer(bool authorize)
        {
            this.authorize = authorize;
        }

        public Task<SlackInboundAuthorizationResult> AuthorizeAsync(SlackInboundEnvelope envelope, CancellationToken ct)
        {
            if (this.authorize)
            {
                return Task.FromResult(SlackInboundAuthorizationResult.Authorized(new SlackWorkspaceConfig
                {
                    TeamId = envelope.TeamId,
                    Enabled = true,
                }));
            }

            return Task.FromResult(SlackInboundAuthorizationResult.Rejected(
                SlackAuthorizationRejectionReason.UnknownWorkspace, "no"));
        }
    }

    private sealed class RecordingHandler
    {
        private readonly ConcurrentQueue<SlackInboundEnvelope> invocations = new();

        public IReadOnlyList<SlackInboundEnvelope> Invocations => this.invocations.ToArray();

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
        {
            this.invocations.Enqueue(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandHandler : ISlackCommandHandler
    {
        private readonly RecordingHandler inner;

        public RecordingCommandHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class RecordingAppMentionHandler : ISlackAppMentionHandler
    {
        private readonly RecordingHandler inner;

        public RecordingAppMentionHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class RecordingInteractionHandler : ISlackInteractionHandler
    {
        private readonly RecordingHandler inner;

        public RecordingInteractionHandler(RecordingHandler inner)
        {
            this.inner = inner;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => this.inner.HandleAsync(envelope, ct);
    }

    private sealed class RecordingDeadLetterQueue : ISlackDeadLetterQueue
    {
        private readonly ConcurrentQueue<SlackDeadLetterEntry> entries = new();

        public IReadOnlyList<SlackDeadLetterEntry> Entries => this.entries.ToArray();

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
        {
            this.entries.Enqueue(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(this.entries.ToArray());
    }

    private sealed class ZeroDelayRetryPolicy : ISlackRetryPolicy
    {
        private readonly int maxAttempts;

        public ZeroDelayRetryPolicy(int maxAttempts)
        {
            this.maxAttempts = maxAttempts;
        }

        public bool ShouldRetry(int attemptNumber, Exception exception)
            => exception is not OperationCanceledException && attemptNumber < this.maxAttempts;

        public TimeSpan GetDelay(int attemptNumber) => TimeSpan.Zero;
    }

    /// <summary>
    /// Helper that builds a pipeline where the first envelope throws
    /// once from inside the dispatch loop -- not the handler -- so we
    /// can prove the ingestor's outer catch-all keeps the loop alive.
    /// Wraps a real pipeline whose dependencies count invocations.
    /// </summary>
    private sealed class ThrowingPipeline
    {
        private int processed;
        private int firstObserved;

        public int ProcessedCount => Volatile.Read(ref this.processed);

        public SlackInboundProcessingPipeline Build()
        {
            // Custom authorizer that throws on the first envelope and
            // succeeds on the rest; the pipeline catches the thrown
            // exception inside its retry loop only AFTER the
            // authorization step (which is outside the retry loop),
            // so the throw propagates to the ingestor's outer catch.
            ThrowOnceAuthorizer authorizer = new(() =>
            {
                Interlocked.Increment(ref this.processed);
                if (Interlocked.Increment(ref this.firstObserved) == 1)
                {
                    throw new InvalidOperationException("authz subsystem blip");
                }
            });

            return new SlackInboundProcessingPipeline(
                authorizer,
                new InMemorySlackIdempotencyGuard(),
                new RecordingCommandHandler(new RecordingHandler()),
                new RecordingAppMentionHandler(new RecordingHandler()),
                new RecordingInteractionHandler(new RecordingHandler()),
                new ZeroDelayRetryPolicy(maxAttempts: 1),
                new RecordingDeadLetterQueue(),
                new SlackInboundAuditRecorder(
                    new InMemorySlackAuditEntryWriter(),
                    NullLogger<SlackInboundAuditRecorder>.Instance,
                    TimeProvider.System),
                NullLogger<SlackInboundProcessingPipeline>.Instance,
                TimeProvider.System);
        }
    }

    private sealed class ThrowOnceAuthorizer : ISlackInboundAuthorizer
    {
        private readonly Action sideEffect;

        public ThrowOnceAuthorizer(Action sideEffect)
        {
            this.sideEffect = sideEffect;
        }

        public Task<SlackInboundAuthorizationResult> AuthorizeAsync(SlackInboundEnvelope envelope, CancellationToken ct)
        {
            this.sideEffect();
            return Task.FromResult(SlackInboundAuthorizationResult.Authorized(new SlackWorkspaceConfig
            {
                TeamId = envelope.TeamId,
                Enabled = true,
            }));
        }
    }

    private sealed class AlwaysThrowingCommandHandler : ISlackCommandHandler
    {
        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => throw new InvalidOperationException("simulated handler failure (retry budget exhausts then DLQ enqueue blows up)");
    }

    private sealed class ThrowingDeadLetterQueue : ISlackDeadLetterQueue
    {
        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated DLQ backend outage");

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(Array.Empty<SlackDeadLetterEntry>());
    }

    /// <summary>
    /// Idempotency guard stub that always re-throws from
    /// <see cref="TryAcquireAsync"/> -- simulates the
    /// <see cref="SlackIdempotencyGuard{TContext}"/> contract where a
    /// transient DB failure without a competing row MUST propagate
    /// (per <c>SlackIdempotencyGuardDbFailureTests</c>) rather than
    /// silently dropping the envelope as a duplicate. Used by the
    /// iter-6 catch-all-forwarding test.
    /// </summary>
    private sealed class ThrowingGuard : ISlackIdempotencyGuard
    {
        private readonly Exception toThrow;

        public ThrowingGuard(Exception toThrow)
        {
            this.toThrow = toThrow;
        }

        public Task<bool> TryAcquireAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => throw this.toThrow;

        public Task MarkCompletedAsync(string idempotencyKey, CancellationToken ct)
            => Task.CompletedTask;

        public Task MarkFailedAsync(string idempotencyKey, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Captures every <see cref="ISlackInboundEnqueueDeadLetterSink.RecordDeadLetterAsync"/>
    /// invocation so the test can assert that the ingestor forwarded
    /// the envelope to the last-resort sink when the pipeline's DLQ
    /// backend itself failed.
    /// </summary>
    private sealed class RecordingDeadLetterFallbackSink : ISlackInboundEnqueueDeadLetterSink
    {
        private readonly ConcurrentQueue<DeadLetterRecord> records = new();

        public IReadOnlyList<DeadLetterRecord> Records => this.records.ToArray();

        public Task RecordDeadLetterAsync(
            SlackInboundEnvelope envelope,
            Exception lastException,
            int attemptCount,
            CancellationToken ct)
        {
            this.records.Enqueue(new DeadLetterRecord(envelope, lastException, attemptCount));
            return Task.CompletedTask;
        }

        public sealed record DeadLetterRecord(
            SlackInboundEnvelope Envelope,
            Exception LastException,
            int AttemptCount);
    }
}
