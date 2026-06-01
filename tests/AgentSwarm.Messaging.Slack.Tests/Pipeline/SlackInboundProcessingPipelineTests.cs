// -----------------------------------------------------------------------
// <copyright file="SlackInboundProcessingPipelineTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Stage 4.3 brief-mandated test scenarios for the inbound pipeline.
/// </summary>
public sealed class SlackInboundProcessingPipelineTests
{
    // ---------------------------------------------------------------
    // Scenario 1: "First event processes normally" -- handler is
    // dispatched, dedup row is marked completed, audit captures
    // outcome = success.
    // ---------------------------------------------------------------
    [Fact]
    public async Task First_command_envelope_dispatches_to_command_handler_and_marks_completed_with_success_audit()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-happy");

        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Processed);
        harness.CommandHandler.Invocations.Should().ContainSingle();
        harness.AppMentionHandler.Invocations.Should().BeEmpty();
        harness.InteractionHandler.Invocations.Should().BeEmpty();
        harness.Guard.Snapshot[envelope.IdempotencyKey].ProcessingStatus
            .Should().Be(SlackInboundRequestProcessingStatus.Completed);
        harness.AuditWriter.Entries.Should().ContainSingle()
            .Which.Should().Match<SlackAuditEntry>(e =>
                e.Outcome == "success"
                && e.RequestType == "slash_command"
                && e.CorrelationId == envelope.IdempotencyKey);
        harness.DeadLetterQueue.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task App_mention_event_dispatches_to_app_mention_handler()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildEventEnvelope(
            "event:Ev-mention",
            rawPayload: """{"type":"event_callback","event_id":"Ev-mention","team_id":"T1","event":{"type":"app_mention","channel":"C1","user":"U1"}}""");

        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Processed);
        harness.AppMentionHandler.Invocations.Should().ContainSingle();
        harness.CommandHandler.Invocations.Should().BeEmpty();
        harness.AuditWriter.Entries.Should().ContainSingle()
            .Which.RequestType.Should().Be("app_mention");
    }

    [Fact]
    public async Task Non_app_mention_event_is_acknowledged_without_dispatch()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildEventEnvelope(
            "event:Ev-message",
            rawPayload: """{"type":"event_callback","event_id":"Ev-message","team_id":"T1","event":{"type":"message","channel":"C1","user":"U1"}}""");

        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Processed,
            "Stage 4.3 ingestor MUST claim the dedup row even for non-app_mention events");
        harness.AppMentionHandler.Invocations.Should().BeEmpty();
        harness.CommandHandler.Invocations.Should().BeEmpty();
        harness.InteractionHandler.Invocations.Should().BeEmpty();
        harness.Guard.Snapshot[envelope.IdempotencyKey].ProcessingStatus
            .Should().Be(SlackInboundRequestProcessingStatus.Completed);
    }

    [Fact]
    public async Task Interaction_envelope_dispatches_to_interaction_handler()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildInteractionEnvelope("interact:T1:U1:view-42:trig-42");

        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Processed);
        harness.InteractionHandler.Invocations.Should().ContainSingle();
        harness.AuditWriter.Entries.Should().ContainSingle()
            .Which.RequestType.Should().Be("interaction");
    }

    // ---------------------------------------------------------------
    // Scenario 2: "Duplicate event is dropped" -- envelope dispatched
    // once, second observation produces duplicate audit row.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Duplicate_envelope_is_dropped_silently_and_writes_duplicate_audit_row()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-dup");

        await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        // Second observation of the same envelope -- Slack retried,
        // or a sibling replica re-dequeued the same payload.
        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Duplicate);
        harness.CommandHandler.Invocations.Should().ContainSingle(
            "the handler must run exactly once across both observations");
        harness.AuditWriter.Entries.Should().HaveCount(2,
            "the success row from the first call, plus the duplicate row from the second");
        harness.AuditWriter.Entries.Last().Outcome.Should().Be("duplicate");
        harness.AuditWriter.Entries.Last().RequestType.Should().Be("slash_command");
        harness.AuditWriter.Entries.Last().CorrelationId.Should().Be(envelope.IdempotencyKey);
    }

    [Fact]
    public async Task Duplicate_detection_handles_preexisting_modal_opened_row_from_fast_path()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-modal");
        // Simulate Stage 4.1 fast-path having already opened the modal.
        harness.Guard.Preload(envelope.IdempotencyKey, SlackInboundRequestProcessingStatus.ModalOpened);

        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Duplicate);
        harness.CommandHandler.Invocations.Should().BeEmpty();
        harness.AuditWriter.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be("duplicate");
        harness.Guard.Snapshot[envelope.IdempotencyKey].ProcessingStatus.Should().Be(
            SlackInboundRequestProcessingStatus.ModalOpened,
            "duplicate path MUST NOT downgrade the fast-path terminal status");
    }

    // ---------------------------------------------------------------
    // Scenario 3: "Failed processing retries" -- handler keeps throwing,
    // retried up to MaxAttempts, then moved to DLQ.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Transient_failures_retry_up_to_max_attempts_then_dead_letter_with_error_audit()
    {
        TestHarness harness = new(maxAttempts: 3);
        harness.CommandHandler.ThrowOnEachCall(new InvalidOperationException("downstream-explodes"));
        SlackInboundEnvelope envelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-bad");

        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.DeadLettered);
        harness.CommandHandler.Invocations.Should().HaveCount(3, "MaxAttempts attempts before DLQ");
        harness.DeadLetterQueue.Entries.Should().ContainSingle();
        SlackDeadLetterEntry dlq = harness.DeadLetterQueue.Entries.Single();
        dlq.Source.Should().Be(SlackDeadLetterSource.Inbound);
        dlq.AttemptCount.Should().Be(3);
        dlq.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        dlq.CorrelationId.Should().Be(envelope.IdempotencyKey);
        dlq.AsInbound().Should().Be(envelope);

        harness.Guard.Snapshot[envelope.IdempotencyKey].ProcessingStatus
            .Should().Be(SlackInboundRequestProcessingStatus.Failed);

        harness.AuditWriter.Entries.Should().ContainSingle()
            .Which.Should().Match<SlackAuditEntry>(e =>
                e.Outcome == "error" && e.ErrorDetail == "downstream-explodes");
    }

    [Fact]
    public async Task Handler_succeeding_on_second_attempt_does_not_dead_letter()
    {
        TestHarness harness = new(maxAttempts: 5);
        harness.CommandHandler.ThrowOnFirstNCalls(1, new InvalidOperationException("flaky"));
        SlackInboundEnvelope envelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-flaky");

        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Processed);
        harness.CommandHandler.Invocations.Should().HaveCount(2);
        harness.DeadLetterQueue.Entries.Should().BeEmpty();
        harness.Guard.Snapshot[envelope.IdempotencyKey].ProcessingStatus
            .Should().Be(SlackInboundRequestProcessingStatus.Completed);
        harness.AuditWriter.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be("success");
    }

    [Fact]
    public async Task Unauthorized_envelope_is_rejected_before_idempotency_or_dispatch()
    {
        TestHarness harness = new(authorize: false);
        SlackInboundEnvelope envelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-unauth");

        SlackInboundProcessingOutcome outcome = await harness.Pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Unauthorized);
        harness.Guard.Snapshot.Should().NotContainKey(envelope.IdempotencyKey,
            "authorization MUST run BEFORE the idempotency row is acquired");
        harness.CommandHandler.Invocations.Should().BeEmpty();
        harness.AuditWriter.Entries.Should().BeEmpty(
            "the rejected_auth audit row is written via the authorizer's SlackAuthorizationAuditSink, NOT the inbound recorder");
    }

    // ---------------------------------------------------------------
    // Reliability regression: DLQ enqueue failure MUST surface as
    // SlackInboundDeadLetterEnqueueException AND MUST NOT silently
    // mark the dedup row as Failed. Stamping Failed when the DLQ
    // backend is unhealthy would lose the poison message because a
    // future Slack retry would silently dedup against the Failed row
    // even though the payload was never persisted to the DLQ.
    // ---------------------------------------------------------------
    [Fact]
    public async Task When_retry_exhausted_and_dlq_enqueue_fails_pipeline_throws_and_leaves_dedup_row_in_processing()
    {
        TestHarness harness = new(maxAttempts: 2);
        ThrowingDeadLetterQueue throwingDlq = new();

        // Rebuild the pipeline with the throwing DLQ; keep every
        // other dependency identical so the assertions speak to the
        // DLQ-failure path alone.
        SlackInboundProcessingPipeline pipelineWithBrokenDlq = new(
            harness.Authorizer,
            harness.Guard,
            new RecordingCommandHandler(harness.CommandHandler),
            new RecordingAppMentionHandler(harness.AppMentionHandler),
            new RecordingInteractionHandler(harness.InteractionHandler),
            harness.RetryPolicy,
            throwingDlq,
            harness.AuditRecorder,
            NullLogger<SlackInboundProcessingPipeline>.Instance,
            TimeProvider.System);

        SlackInboundEnvelope envelope = BuildCommandEnvelope("cmd:T1:U1:/agent:trig-dlqfail");

        // Configure the recording command handler to always throw so
        // the retry budget is exhausted and the pipeline tries to DLQ.
        harness.CommandHandler.ThrowOnEachCall(new InvalidOperationException("handler poisoned"));

        Func<Task> act = () => pipelineWithBrokenDlq.ProcessAsync(envelope, CancellationToken.None);

        FluentAssertions.Specialized.ExceptionAssertions<SlackInboundDeadLetterEnqueueException> thrown =
            (await act
                .Should()
                .ThrowAsync<SlackInboundDeadLetterEnqueueException>())
            .Where(ex => ex.AttemptCount == 2)
            .Where(ex => ex.IdempotencyKey == envelope.IdempotencyKey);

        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>(
            "the DLQ backend's original exception MUST be preserved as InnerException for ops triage");
        thrown.Which.Message.Should().Contain("processing",
            "the exception message MUST surface that the dedup row was left in 'processing' for operator recovery");

        throwingDlq.EnqueueAttempts.Should().Be(1,
            "DLQ enqueue is attempted exactly once -- the pipeline does not retry DLQ enqueue itself");
        harness.Guard.Snapshot[envelope.IdempotencyKey].ProcessingStatus
            .Should().Be(SlackInboundRequestProcessingStatus.Processing,
                "stamping Failed when the DLQ backend is unhealthy would lose the poison message; "
                + "leaving the dedup row in 'processing' signals operators that the envelope is stuck mid-flow");
        harness.AuditWriter.Entries.Should().BeEmpty(
            "no error audit is written because the envelope did NOT reach terminal disposition; "
            + "the audit row would falsely suggest the DLQ contains the payload when it does not");
    }

    // ---------------------------------------------------------------
    // Harness + fakes
    // ---------------------------------------------------------------
    private sealed class TestHarness
    {
        public TestHarness(int maxAttempts = 3, bool authorize = true)
        {
            this.Authorizer = new FakeAuthorizer(authorize);
            this.Guard = new InMemorySlackIdempotencyGuard();
            this.CommandHandler = new RecordingHandler();
            this.AppMentionHandler = new RecordingHandler();
            this.InteractionHandler = new RecordingHandler();
            this.RetryPolicy = new FixedRetryPolicy(maxAttempts);
            this.DeadLetterQueue = new RecordingDeadLetterQueue();
            this.AuditWriter = new InMemorySlackAuditEntryWriter();
            this.AuditRecorder = new SlackInboundAuditRecorder(
                this.AuditWriter,
                NullLogger<SlackInboundAuditRecorder>.Instance,
                TimeProvider.System);

            this.Pipeline = new SlackInboundProcessingPipeline(
                this.Authorizer,
                this.Guard,
                new RecordingCommandHandler(this.CommandHandler),
                new RecordingAppMentionHandler(this.AppMentionHandler),
                new RecordingInteractionHandler(this.InteractionHandler),
                this.RetryPolicy,
                this.DeadLetterQueue,
                this.AuditRecorder,
                NullLogger<SlackInboundProcessingPipeline>.Instance,
                TimeProvider.System);
        }

        public FakeAuthorizer Authorizer { get; }

        public InMemorySlackIdempotencyGuard Guard { get; }

        public RecordingHandler CommandHandler { get; }

        public RecordingHandler AppMentionHandler { get; }

        public RecordingHandler InteractionHandler { get; }

        public FixedRetryPolicy RetryPolicy { get; }

        public RecordingDeadLetterQueue DeadLetterQueue { get; }

        public InMemorySlackAuditEntryWriter AuditWriter { get; }

        public SlackInboundAuditRecorder AuditRecorder { get; }

        public SlackInboundProcessingPipeline Pipeline { get; }
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
                SlackAuthorizationRejectionReason.UnknownWorkspace, "fake-unauth"));
        }
    }

    private sealed class RecordingHandler
    {
        private readonly ConcurrentQueue<SlackInboundEnvelope> invocations = new();
        private Exception? throwOnEachCall;
        private int throwOnFirstN;

        public IReadOnlyList<SlackInboundEnvelope> Invocations => this.invocations.ToArray();

        public void ThrowOnEachCall(Exception ex) => this.throwOnEachCall = ex;

        public void ThrowOnFirstNCalls(int n, Exception ex)
        {
            this.throwOnFirstN = n;
            this.throwOnEachCall = ex;
        }

        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
        {
            this.invocations.Enqueue(envelope);

            if (this.throwOnEachCall is not null)
            {
                if (this.throwOnFirstN == 0)
                {
                    // Throws every call.
                    throw this.throwOnEachCall;
                }

                if (this.invocations.Count <= this.throwOnFirstN)
                {
                    throw this.throwOnEachCall;
                }
            }

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

    private sealed class FixedRetryPolicy : ISlackRetryPolicy
    {
        private readonly int maxAttempts;

        public FixedRetryPolicy(int maxAttempts)
        {
            this.maxAttempts = maxAttempts;
        }

        public bool ShouldRetry(int attemptNumber, Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return false;
            }

            return attemptNumber < this.maxAttempts;
        }

        public TimeSpan GetDelay(int attemptNumber) => TimeSpan.Zero;
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

    private sealed class ThrowingDeadLetterQueue : ISlackDeadLetterQueue
    {
        private int enqueueAttempts;

        public int EnqueueAttempts => this.enqueueAttempts;

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
        {
            Interlocked.Increment(ref this.enqueueAttempts);
            throw new InvalidOperationException(
                "simulated DLQ backend outage -- the underlying ISlackDeadLetterQueue rejected the enqueue.");
        }

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(Array.Empty<SlackDeadLetterEntry>());
    }

    private static SlackInboundEnvelope BuildCommandEnvelope(string key) => new(
        IdempotencyKey: key,
        SourceType: SlackInboundSourceType.Command,
        TeamId: "T1",
        ChannelId: "C1",
        UserId: "U1",
        RawPayload: "team_id=T1&user_id=U1&command=/agent&trigger_id=trig",
        TriggerId: "trig",
        ReceivedAt: DateTimeOffset.UtcNow);

    private static SlackInboundEnvelope BuildEventEnvelope(string key, string rawPayload) => new(
        IdempotencyKey: key,
        SourceType: SlackInboundSourceType.Event,
        TeamId: "T1",
        ChannelId: "C1",
        UserId: "U1",
        RawPayload: rawPayload,
        TriggerId: null,
        ReceivedAt: DateTimeOffset.UtcNow);

    private static SlackInboundEnvelope BuildInteractionEnvelope(string key) => new(
        IdempotencyKey: key,
        SourceType: SlackInboundSourceType.Interaction,
        TeamId: "T1",
        ChannelId: "C1",
        UserId: "U1",
        RawPayload: """{"type":"block_actions","team":{"id":"T1"},"user":{"id":"U1"},"trigger_id":"trig-42","actions":[{"action_id":"approve","value":"yes"}]}""",
        TriggerId: "trig-42",
        ReceivedAt: DateTimeOffset.UtcNow);
}
