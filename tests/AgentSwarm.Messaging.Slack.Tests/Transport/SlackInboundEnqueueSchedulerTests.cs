// -----------------------------------------------------------------------
// <copyright file="SlackInboundEnqueueSchedulerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.1 iter-3 evaluator item 2 fix-pin: post-ACK enqueue MUST
/// retry on transient failures AND surface terminal failures through
/// <see cref="ISlackInboundEnqueueDeadLetterSink"/> so they are
/// recoverable / observable beyond a single <c>LogError</c> line.
/// </summary>
public sealed class SlackInboundEnqueueSchedulerTests
{
    [Fact]
    public async Task First_attempt_success_enqueues_without_dead_letter()
    {
        ChannelBasedSlackInboundQueue queue = new();
        RecordingDeadLetterSink sink = new();
        SlackInboundEnvelope envelope = BuildEnvelope("first");

        await SlackInboundEnqueueScheduler.EnqueueWithRetryAsync(
            queue,
            envelope,
            NullLogger.Instance,
            sink);

        // Envelope landed in the queue.
        using CancellationTokenSource cts = new(500);
        SlackInboundEnvelope dequeued = await queue.DequeueAsync(cts.Token);
        dequeued.IdempotencyKey.Should().Be(envelope.IdempotencyKey);

        sink.Records.Should().BeEmpty(
            "the first attempt succeeded; the dead-letter sink must NOT be invoked");
    }

    [Fact]
    public async Task Transient_failure_recovers_on_retry_and_does_not_dead_letter()
    {
        FlakyQueue flaky = new(failuresBeforeSuccess: 1);
        RecordingDeadLetterSink sink = new();
        SlackInboundEnvelope envelope = BuildEnvelope("flaky");

        await SlackInboundEnqueueScheduler.EnqueueWithRetryAsync(
            flaky,
            envelope,
            NullLogger.Instance,
            sink);

        flaky.EnqueueAttempts.Should().Be(2,
            "the scheduler retries on transient failures so a single blip cannot lose an envelope");
        flaky.Enqueued.Should().HaveCount(1);
        flaky.Enqueued[0].IdempotencyKey.Should().Be(envelope.IdempotencyKey);

        sink.Records.Should().BeEmpty(
            "recovery on retry must NOT invoke the dead-letter sink");
    }

    [Fact]
    public async Task All_attempts_fail_dead_letters_envelope_with_last_exception_and_attempt_count()
    {
        AlwaysFailQueue queue = new();
        RecordingDeadLetterSink sink = new();
        SlackInboundEnvelope envelope = BuildEnvelope("doomed");

        await SlackInboundEnqueueScheduler.EnqueueWithRetryAsync(
            queue,
            envelope,
            NullLogger.Instance,
            sink);

        queue.EnqueueAttempts.Should().Be(SlackInboundEnqueueScheduler.MaxAttempts,
            "the scheduler MUST exhaust its retry budget before dead-lettering");

        sink.Records.Should().ContainSingle(
            "exactly one dead-letter record must be written per terminally-failed envelope");
        DeadLetterRecord record = sink.Records[0];
        record.Envelope.IdempotencyKey.Should().Be(envelope.IdempotencyKey);
        record.AttemptCount.Should().Be(SlackInboundEnqueueScheduler.MaxAttempts);
        record.LastException.Should().BeOfType<InvalidOperationException>();
        record.LastException.Message.Should().Contain("simulated terminal failure");
    }

    [Fact]
    public async Task In_memory_default_sink_captures_dead_letters_for_operator_drain()
    {
        AlwaysFailQueue queue = new();
        InMemorySlackInboundEnqueueDeadLetterSink sink =
            new(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance);
        SlackInboundEnvelope envelope = BuildEnvelope("drainable");

        await SlackInboundEnqueueScheduler.EnqueueWithRetryAsync(
            queue,
            envelope,
            NullLogger.Instance,
            sink);

        sink.DeadLetterCount.Should().Be(1,
            "the in-memory sink MUST track every dead-letter for metrics consumption");

        IReadOnlyList<SlackInboundDeadLetterEntry> drained = sink.DrainCaptured();
        drained.Should().ContainSingle();
        drained[0].Envelope.IdempotencyKey.Should().Be(envelope.IdempotencyKey);
        drained[0].AttemptCount.Should().Be(SlackInboundEnqueueScheduler.MaxAttempts);
        drained[0].ExceptionType.Should().Contain("InvalidOperationException");

        // After drain the queue is empty (callers receive each entry once).
        sink.DrainCaptured().Should().BeEmpty(
            "DrainCaptured must clear the internal ring buffer so the same entry is not re-processed");
    }

    [Fact]
    public async Task Dead_letter_sink_failure_is_logged_critical_but_does_not_throw()
    {
        AlwaysFailQueue queue = new();
        ThrowingDeadLetterSink sink = new();
        SlackInboundEnvelope envelope = BuildEnvelope("worst-case");

        Func<Task> act = async () =>
        {
            await SlackInboundEnqueueScheduler.EnqueueWithRetryAsync(
                queue,
                envelope,
                NullLogger.Instance,
                sink);
        };

        // Even when the sink itself throws, the scheduler MUST NOT
        // propagate (the HTTP response has already been ACKed). The
        // failure is logged Critical inside the scheduler.
        await act.Should().NotThrowAsync(
            "the scheduler runs inside an HttpResponse.OnCompleted callback whose exceptions cannot reach the caller");

        sink.InvocationCount.Should().Be(1);
    }

    private static SlackInboundEnvelope BuildEnvelope(string tag)
        => new(
            IdempotencyKey: $"event:{tag}",
            SourceType: SlackInboundSourceType.Event,
            TeamId: "T0",
            ChannelId: "C0",
            UserId: "U0",
            RawPayload: "{}",
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

    private sealed record DeadLetterRecord(
        SlackInboundEnvelope Envelope,
        Exception LastException,
        int AttemptCount);

    private sealed class RecordingDeadLetterSink : ISlackInboundEnqueueDeadLetterSink
    {
        public List<DeadLetterRecord> Records { get; } = new();

        public Task RecordDeadLetterAsync(
            SlackInboundEnvelope envelope,
            Exception lastException,
            int attemptCount,
            CancellationToken ct)
        {
            this.Records.Add(new DeadLetterRecord(envelope, lastException, attemptCount));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDeadLetterSink : ISlackInboundEnqueueDeadLetterSink
    {
        public int InvocationCount { get; private set; }

        public Task RecordDeadLetterAsync(
            SlackInboundEnvelope envelope,
            Exception lastException,
            int attemptCount,
            CancellationToken ct)
        {
            this.InvocationCount++;
            throw new InvalidOperationException("dead-letter sink intentionally fails (worst-case test).");
        }
    }

    private sealed class FlakyQueue : ISlackInboundQueue
    {
        private readonly int failuresBeforeSuccess;

        public FlakyQueue(int failuresBeforeSuccess)
        {
            this.failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int EnqueueAttempts { get; private set; }

        public List<SlackInboundEnvelope> Enqueued { get; } = new();

        public ValueTask EnqueueAsync(SlackInboundEnvelope envelope)
        {
            this.EnqueueAttempts++;
            if (this.EnqueueAttempts <= this.failuresBeforeSuccess)
            {
                throw new InvalidOperationException(
                    $"simulated transient failure (attempt {this.EnqueueAttempts}).");
            }

            this.Enqueued.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public ValueTask<SlackInboundEnvelope> DequeueAsync(CancellationToken ct)
            => throw new NotSupportedException("FlakyQueue is enqueue-only for these tests.");
    }

    private sealed class AlwaysFailQueue : ISlackInboundQueue
    {
        public int EnqueueAttempts { get; private set; }

        public ValueTask EnqueueAsync(SlackInboundEnvelope envelope)
        {
            this.EnqueueAttempts++;
            throw new InvalidOperationException(
                $"simulated terminal failure (attempt {this.EnqueueAttempts}).");
        }

        public ValueTask<SlackInboundEnvelope> DequeueAsync(CancellationToken ct)
            => throw new NotSupportedException("AlwaysFailQueue is enqueue-only for these tests.");
    }
}
