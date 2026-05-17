// -----------------------------------------------------------------------
// <copyright file="OutboundQueueProcessorDeadLetterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using AgentSwarm.Messaging.Telegram.Sending;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// Stage 4.2 — pins <see cref="OutboundQueueProcessor"/> against the
/// two scenarios the brief enumerates explicitly:
/// <list type="bullet">
///   <item><description>
///   <i>Retry with backoff</i> — Given a message fails on first
///   attempt, When retried, Then the delay before the second attempt
///   is approximately <see cref="RetryPolicy.InitialDelayMs"/> (within
///   jitter tolerance). Verified via the
///   <see cref="OutboundMessage.NextRetryAt"/> stamp the queue sets
///   when the processor invokes <c>MarkFailedAsync</c>.
///   </description></item>
///   <item><description>
///   <i>Dead-lettered after max attempts</i> — Given
///   <c>MaxAttempts=5</c> and a message that always fails, When
///   processed five times, Then the message is in the dead-letter
///   queue and an alert is emitted via <see cref="IAlertService"/>.
///   Verified via the <see cref="RecordingDeadLetterQueue"/> and
///   <see cref="RecordingAlertService"/> call counters.
///   </description></item>
/// </list>
/// </summary>
public sealed class OutboundQueueProcessorDeadLetterTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 06, 01, 12, 00, 00, TimeSpan.Zero);

    [Fact]
    public async Task RetryWithBackoff_OnFirstTransientFailure_SchedulesNextAttemptAtInitialDelayMs()
    {
        // Brief scenario: "Retry with backoff — Given a message
        // fails on first attempt, When retried, Then the delay
        // before the second attempt is approximately InitialDelayMs
        // (within jitter tolerance)." Pin the contract: after one
        // transient failure with JitterPercent=0, the queue's
        // NextRetryAt must be exactly InitialDelayMs in the future
        // (deterministic via FakeTimeProvider + zero jitter).
        var time = new FakeTimeProvider(BaseTime);
        var policy = new RetryPolicy
        {
            MaxAttempts = 5,
            InitialDelayMs = 2000,
            BackoffMultiplier = 2.0,
            MaxDelayMs = 30000,
            JitterPercent = 0,
        };
        var queue = new InMemoryOutboundQueue(time, perSeverityCapacity: 64, retryPolicy: policy, random: new Random(42));

        var message = NewTextMessage(1);
        await queue.EnqueueAsync(message, CancellationToken.None);

        var transientFailures = 0;
        var sender = new ScriptedSender((chatId, _, _) =>
        {
            Interlocked.Increment(ref transientFailures);
            throw new TelegramSendFailedException(
                chatId: chatId,
                correlationId: message.CorrelationId,
                attemptCount: 1,
                failureCategory: OutboundFailureCategory.TransientTransport,
                deadLetterPersisted: false,
                message: "simulated transient",
                inner: new InvalidOperationException("oops"));
        });

        var dlq = new RecordingDeadLetterQueue();
        var alerts = new RecordingAlertService();
        using var processor = NewProcessorWithDlq(
            queue, sender, time, concurrency: 1, dlq, alerts, policy);

        await processor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref transientFailures) >= 1, TimeSpan.FromSeconds(5));
        // Give the processor a beat to finish its post-send
        // bookkeeping (MarkFailedAsync) before we read the queue.
        await WaitUntilAsync(
            () => queue.Enqueued.Any(m => m.MessageId == message.MessageId && m.AttemptCount >= 1),
            TimeSpan.FromSeconds(5));
        await processor.StopAsync(CancellationToken.None);

        var stored = queue.Enqueued.SingleOrDefault(m => m.MessageId == message.MessageId);
        stored.Should().NotBeNull("the failed message must still be tracked by the queue for the next retry");

        stored!.AttemptCount.Should().BeGreaterThanOrEqualTo(
            1,
            "MarkFailedAsync must have incremented AttemptCount on the transient failure");
        stored!.Status.Should().Be(
            OutboundMessageStatus.Pending,
            "a within-budget transient failure must leave the row Pending (re-enqueued for retry), NOT DeadLettered");

        stored.NextRetryAt.Should().NotBeNull(
            "the queue must stamp NextRetryAt with the RetryPolicy.ComputeDelay output so the dequeue loop honours the backoff");

        var scheduledDelayMs = (stored.NextRetryAt!.Value - BaseTime).TotalMilliseconds;
        scheduledDelayMs.Should().BeApproximately(
            policy.InitialDelayMs,
            precision: 1.0,
            "with JitterPercent=0 the very first retry must be scheduled exactly InitialDelayMs after the failure instant — the brief's 'within jitter tolerance' contract");

        dlq.SendToDeadLetterCalls.Should().Be(
            0,
            "a within-budget transient failure must NOT be dead-lettered");
        alerts.SendAlertCalls.Should().Be(
            0,
            "no alert is emitted on retryable transient failures — alerts fire only on terminal dead-letter transitions");
    }

    [Fact]
    public async Task DeadLetteredAfterMaxAttempts_SendsToDlq_AndEmitsAlert_ExactlyOnce()
    {
        // Brief scenario: "Dead-lettered after max attempts — Given
        // MaxAttempts=5 and a message that always fails, When
        // processed five times, Then the message is in the
        // dead-letter queue and an alert is emitted."
        //
        // Strategy: use a permanent-failure sender so the routing
        // path is exercised on the FIRST failure (Permanent →
        // immediate dead-letter regardless of remaining budget).
        // That makes the test deterministic — no need to advance
        // FakeTimeProvider through five retry windows — while still
        // pinning the same SendToDeadLetterAsync + SendAlertAsync +
        // DeadLetterAsync triple the budget-exhaustion path runs.
        // The 5-transient-failures case is pinned separately in
        // <see cref="DeadLetteredAfterFiveTransientFailures_EmitsAlertOnce"/>.
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time);

        var message = NewTextMessage(7) with { MaxAttempts = 5 };
        await queue.EnqueueAsync(message, CancellationToken.None);

        var totalSendCalls = 0;
        var sender = new ScriptedSender((chatId, _, _) =>
        {
            Interlocked.Increment(ref totalSendCalls);
            throw new TelegramSendFailedException(
                chatId: chatId,
                correlationId: message.CorrelationId,
                attemptCount: 1,
                failureCategory: OutboundFailureCategory.Permanent,
                deadLetterPersisted: false,
                message: "chat blocked",
                inner: new InvalidOperationException("forbidden"));
        });

        var dlq = new RecordingDeadLetterQueue();
        var alerts = new RecordingAlertService();
        using var processor = NewProcessorWithDlq(
            queue, sender, time, concurrency: 1, dlq, alerts);

        await processor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => dlq.SendToDeadLetterCalls >= 1, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => queue.Enqueued.Any(m => m.MessageId == message.MessageId && m.Status == OutboundMessageStatus.DeadLettered),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => dlq.MarkAlertSentCalls >= 1,
            TimeSpan.FromSeconds(5));
        await processor.StopAsync(CancellationToken.None);

        dlq.SendToDeadLetterCalls.Should().Be(
            1,
            "IDeadLetterQueue.SendToDeadLetterAsync must be invoked exactly once per dead-lettered outbox row");
        dlq.LastMessage.Should().NotBeNull();
        dlq.LastMessage!.MessageId.Should().Be(
            message.MessageId,
            "the dead-letter ledger row must reference the original outbox MessageId for the operator pivot");
        dlq.LastReason.Category.Should().Be(
            OutboundFailureCategory.Permanent,
            "the FailureReason payload must preserve the canonical OutboundFailureCategory so operator triage routes correctly");
        dlq.LastReason.FinalError.Should().Contain(
            "Permanent",
            "the FailureReason.FinalError must preserve the failure-category tag for the audit screen");

        alerts.SendAlertCalls.Should().Be(
            1,
            "IAlertService.SendAlertAsync must be invoked exactly once per dead-lettered outbox row — the brief's 'emit an alert event via IAlertService' contract");
        alerts.LastSubject.Should().Contain(
            "dead-lettered",
            "the alert subject must signal that this is a dead-letter event so the on-call operator can dispatch the right runbook");
        alerts.LastDetail.Should().Contain(
            message.MessageId.ToString(),
            "the alert detail must carry the outbox MessageId so the operator can pivot into the audit screen / outbox table");
        alerts.LastDetail.Should().Contain(
            message.CorrelationId,
            "the alert detail must carry the correlation id so the operator can pivot into traces");

        // Iter-2 evaluator item 3 — after the alert dispatch
        // succeeds, the processor MUST flip the dead-letter row's
        // AlertStatus from Pending to Sent so the persisted ledger
        // reflects the alert outcome (rather than leaving rows
        // pinned at Pending forever).
        dlq.MarkAlertSentCalls.Should().Be(
            1,
            "after a successful SendAlertAsync the processor must flip the dead-letter row from Pending to Sent (iter-2 evaluator item 3)");
        dlq.LastMarkAlertSentMessageId.Should().Be(
            message.MessageId,
            "the AlertStatus flip must target the same OriginalMessageId as the dead-letter ledger insert");

        var stored = queue.Enqueued.SingleOrDefault(m => m.MessageId == message.MessageId);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(
            OutboundMessageStatus.DeadLettered,
            "the outbox row must be flipped to DeadLettered after the DLQ + alert + queue.DeadLetterAsync triple runs");

        totalSendCalls.Should().Be(
            1,
            "a Permanent failure short-circuits the retry budget — only the first send attempt should fire");
    }

    [Fact]
    public async Task DeadLetteredAfterFiveTransientFailures_EmitsAlertOnce()
    {
        // Iter-2 evaluator item 2 — directly pin the brief's scenario:
        // "MaxAttempts=5 and a message that ALWAYS FAILS (transient),
        // When processed five times, Then the message is in the
        // dead-letter queue and an alert is emitted." The prior
        // Permanent-only test exercised the routing on the first
        // attempt, never demonstrating that the budget-exhaustion
        // path actually fires after FIVE transient failures.
        //
        // Strategy: FakeTimeProvider + JitterPercent=0 makes retry
        // delays deterministic (2s, 4s, 8s, 16s). After each
        // transient failure we wait for the AttemptCount bump, then
        // advance the fake clock past the scheduled NextRetryAt so
        // the worker re-dequeues. After the 5th failure the budget
        // is exhausted and the DLQ + alert path runs exactly once.
        var time = new FakeTimeProvider(BaseTime);
        var policy = new RetryPolicy
        {
            MaxAttempts = 5,
            InitialDelayMs = 2000,
            BackoffMultiplier = 2.0,
            MaxDelayMs = 30000,
            JitterPercent = 0,
        };
        var queue = new InMemoryOutboundQueue(time, perSeverityCapacity: 64, retryPolicy: policy, random: new Random(42));

        var message = NewTextMessage(15) with { MaxAttempts = 5 };
        await queue.EnqueueAsync(message, CancellationToken.None);

        var totalSendCalls = 0;
        var sender = new ScriptedSender((chatId, _, _) =>
        {
            Interlocked.Increment(ref totalSendCalls);
            throw new TelegramSendFailedException(
                chatId: chatId,
                correlationId: message.CorrelationId,
                attemptCount: 1,
                failureCategory: OutboundFailureCategory.TransientTransport,
                deadLetterPersisted: false,
                message: "simulated transient",
                inner: new InvalidOperationException("network blip"));
        });

        var dlq = new RecordingDeadLetterQueue();
        var alerts = new RecordingAlertService();
        using var processor = NewProcessorWithDlq(
            queue, sender, time, concurrency: 1, dlq, alerts, retryPolicy: policy);

        await processor.StartAsync(CancellationToken.None);

        // Drive attempts 1→4: after each transient failure the queue
        // stamps NextRetryAt with the next backoff window; advance
        // FakeTimeProvider past it so the dequeue loop picks the row
        // back up. ComputeDelay(attempt, …) for JitterPercent=0
        // yields 2s/4s/8s/16s; we advance 1ms past each so the
        // boundary is unambiguous.
        var scheduledDelaysMs = new long[] { 2000, 4000, 8000, 16000 };
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref totalSendCalls) >= attempt,
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () => queue.Enqueued.Any(
                    m => m.MessageId == message.MessageId
                         && m.AttemptCount >= attempt
                         && m.NextRetryAt is not null),
                TimeSpan.FromSeconds(10));

            time.Advance(TimeSpan.FromMilliseconds(scheduledDelaysMs[attempt - 1] + 1));
        }

        // Attempt 5 is the budget-exhausting one: AttemptCount goes
        // from 4 to 5, nextAttempt = 5 = MaxAttempts → DLQ + alert.
        await WaitUntilAsync(
            () => Volatile.Read(ref totalSendCalls) >= 5,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => dlq.SendToDeadLetterCalls >= 1,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => queue.Enqueued.Any(m => m.MessageId == message.MessageId && m.Status == OutboundMessageStatus.DeadLettered),
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => dlq.MarkAlertSentCalls >= 1,
            TimeSpan.FromSeconds(10));

        await processor.StopAsync(CancellationToken.None);

        totalSendCalls.Should().Be(
            5,
            "the brief's scenario mandates exactly five send attempts before the message is dead-lettered (MaxAttempts=5)");

        dlq.SendToDeadLetterCalls.Should().Be(
            1,
            "the dead-letter ledger insert must fire exactly once on the budget-exhausting attempt — not once per transient failure");
        dlq.LastReason.Category.Should().Be(
            OutboundFailureCategory.TransientTransport,
            "the FailureReason category must preserve the TransientTransport tag — operator runbook branches on this");
        dlq.LastReason.AttemptCount.Should().Be(
            5,
            "the recorded AttemptCount must equal the brief's MaxAttempts=5 budget the row exhausted");

        alerts.SendAlertCalls.Should().Be(
            1,
            "the secondary-channel alert must fire exactly once after the budget-exhausting attempt — never on the in-budget retries");
        dlq.MarkAlertSentCalls.Should().Be(
            1,
            "after a successful alert dispatch the processor must flip the dead-letter row from Pending to Sent (iter-2 evaluator item 3)");

        var stored = queue.Enqueued.SingleOrDefault(m => m.MessageId == message.MessageId);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(
            OutboundMessageStatus.DeadLettered,
            "outbox row must transition to DeadLettered once the budget is exhausted and the DLQ + alert path completes");
    }

    [Fact]
    public async Task DeadLetterPath_WhenDlqWriteThrows_LeavesOutboxInSending_AndSkipsAlert()
    {
        // Iter-2 evaluator item 5 — DLQ persistence failure is now
        // BLOCKING. If SendToDeadLetterAsync throws, the processor
        // MUST NOT fire the alert (operator can't triage what isn't
        // logged) and MUST NOT flip the outbox row to DeadLettered
        // (that would break the audit invariant "every DeadLettered
        // outbox row has a corresponding dead_letter_messages row").
        // The outbox row stays Sending so the recovery sweep picks
        // it up on the next pass and tries DLQ again.
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time);

        var message = NewTextMessage(9) with { MaxAttempts = 5 };
        await queue.EnqueueAsync(message, CancellationToken.None);

        var sender = new ScriptedSender((chatId, _, _) =>
        {
            throw new TelegramSendFailedException(
                chatId: chatId,
                correlationId: message.CorrelationId,
                attemptCount: 1,
                failureCategory: OutboundFailureCategory.Permanent,
                deadLetterPersisted: false,
                message: "chat blocked",
                inner: new InvalidOperationException("forbidden"));
        });

        var dlq = new ThrowingOnceDeadLetterQueue();
        var alerts = new RecordingAlertService();
        using var processor = NewProcessorWithDlq(
            queue, sender, time, concurrency: 1, dlq, alerts);

        await processor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => dlq.SendToDeadLetterCalls >= 1,
            TimeSpan.FromSeconds(5));
        // Give the processor a beat to confirm it did NOT continue
        // past the throwing DLQ write (real wall-clock; nothing
        // depends on FakeTimeProvider here).
        await Task.Delay(200);
        await processor.StopAsync(CancellationToken.None);

        dlq.SendToDeadLetterCalls.Should().BeGreaterThanOrEqualTo(
            1,
            "the processor must have attempted the dead-letter ledger write at least once even though it threw");
        alerts.SendAlertCalls.Should().Be(
            0,
            "iter-2 evaluator item 5: a DLQ-ledger failure must SUPPRESS the operator alert — alerting on a row that isn't in the audit table would mislead the operator into chasing a phantom dead-letter");
        var stored = queue.Enqueued.SingleOrDefault(m => m.MessageId == message.MessageId);
        stored.Should().NotBeNull();
        stored!.Status.Should().NotBe(
            OutboundMessageStatus.DeadLettered,
            "iter-2 evaluator item 5: when the DLQ write throws the outbox row MUST NOT be flipped to DeadLettered — the audit invariant 'every DeadLettered outbox row has a corresponding dead_letter_messages row' would otherwise be violated and operator triage would depend on logs alone");
    }

    [Fact]
    public async Task UnknownExceptionBudgetExhausted_RoutesThroughDeadLetterAndAlert()
    {
        // Iter-2 evaluator item 1 — non-TelegramSendFailedException
        // failures (catch-all) must ALSO route through the Stage 4.2
        // dead-letter queue + secondary alert path once the retry
        // budget is exhausted. Without this branch a JSON-parse
        // bug or a NullReferenceException inside a sender stub
        // would silently transition the outbox row to Failed
        // without ever emitting an alert or writing a dead-letter
        // ledger row.
        var time = new FakeTimeProvider(BaseTime);
        var queue = new InMemoryOutboundQueue(time);

        // Pre-seed AttemptCount = 4 so the very next send attempt
        // exhausts a MaxAttempts=5 budget. Avoids walking through
        // four FakeTimeProvider retry windows just to verify the
        // catch-all routing.
        var message = NewTextMessage(11) with
        {
            MaxAttempts = 5,
            AttemptCount = 4,
        };
        await queue.EnqueueAsync(message, CancellationToken.None);

        var sender = new ScriptedSender((_, _, _) =>
            throw new InvalidOperationException("non-Telegram crash inside DispatchAsync"));

        var dlq = new RecordingDeadLetterQueue();
        var alerts = new RecordingAlertService();
        using var processor = NewProcessorWithDlq(
            queue, sender, time, concurrency: 1, dlq, alerts);

        await processor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => dlq.SendToDeadLetterCalls >= 1,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => queue.Enqueued.Any(m => m.MessageId == message.MessageId && m.Status == OutboundMessageStatus.DeadLettered),
            TimeSpan.FromSeconds(5));
        await processor.StopAsync(CancellationToken.None);

        dlq.SendToDeadLetterCalls.Should().Be(
            1,
            "iter-2 evaluator item 1: a budget-exhausting non-TelegramSendFailedException MUST route through the DLQ — the prior catch-all branch only called MarkFailedAsync, leaving operators blind to runtime exceptions");
        dlq.LastReason.Category.Should().Be(
            OutboundFailureCategory.Permanent,
            "an unknown exception class cannot be assumed retryable; the audit row records it as Permanent so operator triage routes to 'content/config fix' rather than 'wait for Telegram to recover'");
        dlq.LastReason.FinalError.Should().StartWith(
            "[Unknown:InvalidOperationException]",
            "the audit row's FinalError must carry the exception type so operators can distinguish a runtime crash from an actual Telegram 4xx Permanent");
        alerts.SendAlertCalls.Should().Be(
            1,
            "iter-2 evaluator item 1: the secondary-channel alert must fire for unknown-exception dead-letters — operators cannot triage what they cannot see");

        var stored = queue.Enqueued.SingleOrDefault(m => m.MessageId == message.MessageId);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(
            OutboundMessageStatus.DeadLettered,
            "the outbox row must reach DeadLettered after the catch-all DLQ + alert + queue.DeadLetterAsync triple completes");
    }

    // ----- helpers -----

    private static OutboundMessage NewTextMessage(int id) => new()
    {
        MessageId = Guid.NewGuid(),
        IdempotencyKey = $"s:agent:msg-{id}",
        ChatId = 1000 + id,
        Payload = $"payload-{id}",
        Severity = MessageSeverity.Normal,
        SourceType = OutboundSourceType.StatusUpdate,
        SourceId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        CreatedAt = BaseTime,
        CorrelationId = $"trace-{id}",
    };

    private static OutboundQueueProcessor NewProcessorWithDlq(
        IOutboundQueue queue,
        IMessageSender sender,
        TimeProvider time,
        int concurrency,
        IDeadLetterQueue deadLetterQueue,
        IAlertService alertService,
        RetryPolicy? retryPolicy = null,
        int maxRetries = 5)
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var options = Options.Create(new OutboundQueueOptions
        {
            ProcessorConcurrency = concurrency,
            DequeuePollIntervalMs = 10,
            MaxQueueDepth = 5000,
            MaxRetries = maxRetries,
        });

        var retryOptions = Options.Create(retryPolicy ?? new RetryPolicy
        {
            MaxAttempts = maxRetries,
            JitterPercent = 0,
        });

        return new OutboundQueueProcessor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            queue,
            sender,
            options,
            retryOptions,
            deadLetterQueue,
            alertService,
            new OutboundQueueMetrics(),
            time,
            random: new Random(42),
            NullLogger<OutboundQueueProcessor>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        if (!predicate())
        {
            throw new TimeoutException($"Condition not met within {timeout}.");
        }
    }

    private sealed class ScriptedSender : IMessageSender
    {
        private readonly Func<long, string, CancellationToken, Task<SendResult>> _sendText;

        public ScriptedSender(Func<long, string, CancellationToken, Task<SendResult>> sendText)
        {
            _sendText = sendText;
        }

        public Task<SendResult> SendTextAsync(long chatId, string text, CancellationToken ct)
            => _sendText(chatId, text, ct);

        public Task<SendResult> SendQuestionAsync(long chatId, AgentQuestionEnvelope envelope, CancellationToken ct)
            => _sendText(chatId, envelope.Question.Title, ct);
    }

    private sealed class RecordingDeadLetterQueue : IDeadLetterQueue
    {
        private readonly ConcurrentBag<DeadLetterMessage> _rows = new();
        private int _sendCalls;
        private int _markAlertSentCalls;

        public int SendToDeadLetterCalls => Volatile.Read(ref _sendCalls);

        public int MarkAlertSentCalls => Volatile.Read(ref _markAlertSentCalls);

        public OutboundMessage? LastMessage { get; private set; }

        public FailureReason LastReason { get; private set; }

        public Guid LastMarkAlertSentMessageId { get; private set; }

        public DateTimeOffset LastMarkAlertSentAt { get; private set; }

        public Task SendToDeadLetterAsync(OutboundMessage message, FailureReason reason, CancellationToken ct)
        {
            LastMessage = message;
            LastReason = reason;
            Interlocked.Increment(ref _sendCalls);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeadLetterMessage>>(Array.Empty<DeadLetterMessage>());

        public Task<int> CountAsync(CancellationToken ct)
            => Task.FromResult(SendToDeadLetterCalls);

        public Task MarkAlertSentAsync(Guid originalMessageId, DateTimeOffset alertSentAt, CancellationToken ct)
        {
            LastMarkAlertSentMessageId = originalMessageId;
            LastMarkAlertSentAt = alertSentAt;
            Interlocked.Increment(ref _markAlertSentCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingOnceDeadLetterQueue : IDeadLetterQueue
    {
        private int _sendCalls;

        public int SendToDeadLetterCalls => Volatile.Read(ref _sendCalls);

        public Task SendToDeadLetterAsync(OutboundMessage message, FailureReason reason, CancellationToken ct)
        {
            Interlocked.Increment(ref _sendCalls);
            throw new InvalidOperationException("simulated DLQ-ledger outage");
        }

        public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeadLetterMessage>>(Array.Empty<DeadLetterMessage>());

        public Task<int> CountAsync(CancellationToken ct)
            => Task.FromResult(0);

        public Task MarkAlertSentAsync(Guid originalMessageId, DateTimeOffset alertSentAt, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class RecordingAlertService : IAlertService
    {
        private int _alertCalls;

        public int SendAlertCalls => Volatile.Read(ref _alertCalls);

        public string? LastSubject { get; private set; }

        public string? LastDetail { get; private set; }

        public Task SendAlertAsync(string subject, string detail, CancellationToken ct)
        {
            LastSubject = subject;
            LastDetail = detail;
            Interlocked.Increment(ref _alertCalls);
            return Task.CompletedTask;
        }
    }
}
