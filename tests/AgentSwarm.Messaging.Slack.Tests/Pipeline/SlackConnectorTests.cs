// -----------------------------------------------------------------------
// <copyright file="SlackConnectorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 6.3 unit tests for <see cref="SlackConnector"/>. Pins the
/// render-thread-enqueue handshake the connector contract requires:
/// every send resolves a thread mapping FIRST so the dispatcher can
/// recover the destination channel + team without re-doing that
/// work.
/// </summary>
public sealed class SlackConnectorTests
{
    private const string TeamId = "T-CONN";

    [Fact]
    public async Task SendMessageAsync_renders_and_enqueues_envelope_with_thread_ts()
    {
        RecordingOutboundQueue queue = new();
        RecordingThreadManager threadManager = new(
            new SlackThreadMapping
            {
                TaskId = "TASK-1",
                TeamId = TeamId,
                ChannelId = "C-1",
                ThreadTs = "1700000000.000100",
                AgentId = "agent-x",
                CorrelationId = "corr-1",
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow,
            });
        SlackConnector connector = BuildConnector(queue, threadManager);

        MessengerMessage message = new(
            MessageId: "MSG-1",
            AgentId: "agent-x",
            TaskId: "TASK-1",
            Content: "Hello human",
            MessageType: MessageType.StatusUpdate,
            CorrelationId: "corr-1",
            Timestamp: DateTimeOffset.UtcNow);

        await connector.SendMessageAsync(message, CancellationToken.None);

        threadManager.GetOrCreateCalls.Should().HaveCount(1);
        threadManager.GetOrCreateCalls[0].TaskId.Should().Be("TASK-1");
        threadManager.GetOrCreateCalls[0].TeamId.Should().Be(TeamId);
        threadManager.GetOrCreateCalls[0].AgentId.Should().Be("agent-x");
        threadManager.GetOrCreateCalls[0].CorrelationId.Should().Be("corr-1");

        queue.Enqueued.Should().HaveCount(1);
        SlackOutboundEnvelope env = queue.Enqueued[0];
        env.TaskId.Should().Be("TASK-1");
        env.CorrelationId.Should().Be("corr-1");
        env.MessageType.Should().Be(SlackOutboundOperationKind.PostMessage);
        env.ThreadTs.Should().Be("1700000000.000100");
        env.BlockKitPayload.Should().NotBeNullOrWhiteSpace();
        env.BlockKitPayload.Should().Contain("Hello human");
    }

    [Fact]
    public async Task SendQuestionAsync_renders_and_enqueues_envelope_with_thread_ts()
    {
        RecordingOutboundQueue queue = new();
        RecordingThreadManager threadManager = new(
            new SlackThreadMapping
            {
                TaskId = "TASK-Q",
                TeamId = TeamId,
                ChannelId = "C-Q",
                ThreadTs = "1700000200.000200",
                AgentId = "agent-q",
                CorrelationId = "corr-q",
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow,
            });
        SlackConnector connector = BuildConnector(queue, threadManager);

        AgentQuestion question = new(
            QuestionId: "Q-1",
            AgentId: "agent-q",
            TaskId: "TASK-Q",
            Title: "Approve?",
            Body: "Please confirm the deployment plan.",
            Severity: "warning",
            AllowedActions: new[]
            {
                new HumanAction("approve", "Approve", "approve", false),
                new HumanAction("reject", "Reject", "reject", true),
            },
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId: "corr-q");

        await connector.SendQuestionAsync(question, CancellationToken.None);

        threadManager.GetOrCreateCalls.Should().HaveCount(1);

        queue.Enqueued.Should().HaveCount(1);
        SlackOutboundEnvelope env = queue.Enqueued[0];
        env.TaskId.Should().Be("TASK-Q");
        env.CorrelationId.Should().Be("corr-q");
        env.MessageType.Should().Be(SlackOutboundOperationKind.PostMessage);
        env.ThreadTs.Should().Be("1700000200.000200");
        env.BlockKitPayload.Should().Contain("Approve?");
    }

    [Fact]
    public async Task SendMessageAsync_throws_when_DefaultTeamId_missing()
    {
        RecordingOutboundQueue queue = new();
        RecordingThreadManager threadManager = new(
            new SlackThreadMapping { TaskId = "x", TeamId = "x", ChannelId = "x", ThreadTs = "x" });
        SlackConnector connector = BuildConnector(queue, threadManager, defaultTeamId: null);

        MessengerMessage message = new(
            "MSG-X", "agent-x", "TASK-X", "x", MessageType.StatusUpdate, "corr-x", DateTimeOffset.UtcNow);

        Func<Task> act = async () => await connector.SendMessageAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DefaultTeamId*");
        threadManager.GetOrCreateCalls.Should().BeEmpty(
            "the connector MUST fail fast at the team-id resolution step rather than dragging the renderer in");
        queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task ReceiveAsync_returns_empty_list()
    {
        RecordingOutboundQueue queue = new();
        RecordingThreadManager threadManager = new(
            new SlackThreadMapping { TaskId = "x", TeamId = TeamId, ChannelId = "x", ThreadTs = "x" });
        SlackConnector connector = BuildConnector(queue, threadManager);

        IReadOnlyList<MessengerEvent> events = await connector.ReceiveAsync(CancellationToken.None);

        events.Should().BeEmpty(
            "with no events buffered SlackConnector.ReceiveAsync MUST return an empty list rather than throw or block");
    }

    [Fact]
    public async Task ReceiveAsync_drains_buffered_events_in_fifo_order()
    {
        // Stage 8.1: SlackConnector.ReceiveAsync drains processed
        // inbound events from the ISlackInboundEventBuffer that
        // BufferingAgentTaskServiceDecorator populates. This test
        // bypasses the decorator and writes directly into the buffer
        // so the contract is asserted independently of the decorator
        // wiring.
        RecordingOutboundQueue queue = new();
        RecordingThreadManager threadManager = new(
            new SlackThreadMapping { TaskId = "x", TeamId = TeamId, ChannelId = "x", ThreadTs = "x" });
        InMemorySlackInboundEventBuffer buffer = new();
        SlackConnector connector = BuildConnector(queue, threadManager, buffer: buffer);

        HumanDecisionEvent first = new(
            QuestionId: "Q-1",
            ActionValue: "approve",
            Comment: null,
            Messenger: "slack",
            ExternalUserId: "U-1",
            ExternalMessageId: "1700000000.000001",
            ReceivedAt: DateTimeOffset.UtcNow,
            CorrelationId: "corr-1");
        HumanDecisionEvent second = first with { QuestionId = "Q-2", CorrelationId = "corr-2" };

        buffer.Enqueue(first);
        buffer.Enqueue(second);

        IReadOnlyList<MessengerEvent> drained = await connector.ReceiveAsync(CancellationToken.None);

        drained.Should().HaveCount(2);
        drained[0].Should().BeSameAs(first);
        drained[1].Should().BeSameAs(second);
        buffer.Count.Should().Be(0, "ReceiveAsync MUST consume the events it returns");

        IReadOnlyList<MessengerEvent> drainedAgain = await connector.ReceiveAsync(CancellationToken.None);
        drainedAgain.Should().BeEmpty("a second poll with no new events MUST return empty rather than re-deliver");
    }

    [Fact]
    public async Task SendMessageAsync_throws_ArgumentNullException_on_null_message()
    {
        // Iter-3 evaluator item 1 fix: the assertion MUST be awaited.
        // The prior `act.Should().ThrowAsync<>()` call returned a
        // dangling Task that the test discarded, so the assertion
        // never actually executed (the test would have passed even if
        // SendMessageAsync silently swallowed the null input). Making
        // the test `async Task` and awaiting the assertion forces
        // FluentAssertions to drive the throw probe to completion.
        RecordingOutboundQueue queue = new();
        RecordingThreadManager threadManager = new(null);
        SlackConnector connector = BuildConnector(queue, threadManager);

        Func<Task> act = async () => await connector.SendMessageAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SendQuestionAsync_throws_ArgumentNullException_on_null_question()
    {
        // Iter-3 evaluator item 1 fix: see SendMessageAsync sibling.
        RecordingOutboundQueue queue = new();
        RecordingThreadManager threadManager = new(null);
        SlackConnector connector = BuildConnector(queue, threadManager);

        Func<Task> act = async () => await connector.SendQuestionAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Cancellation_propagates_OperationCanceledException()
    {
        RecordingOutboundQueue queue = new();
        RecordingThreadManager threadManager = new(
            new SlackThreadMapping { TaskId = "x", TeamId = TeamId, ChannelId = "x", ThreadTs = "x" });
        SlackConnector connector = BuildConnector(queue, threadManager);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        MessengerMessage message = new(
            "MSG-X", "agent-x", "TASK-X", "x", MessageType.StatusUpdate, "corr-x", DateTimeOffset.UtcNow);

        Func<Task> act = async () => await connector.SendMessageAsync(message, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendMessageAsync_writes_connector_level_audit_row()
    {
        // Stage 8.1 iter-2 item 1: SlackConnector composes
        // ISlackAuditLogger and emits one connector-level
        // message_send audit row per successful enqueue so operators
        // can trace "orchestrator asked the connector to send"
        // independently of the dispatcher's per-HTTP audit row.
        RecordingOutboundQueue queue = new();
        SlackThreadMapping mapping = new()
        {
            TaskId = "TASK-AUDIT",
            TeamId = TeamId,
            ChannelId = "C-AUDIT",
            ThreadTs = "1700000300.000300",
            AgentId = "agent-audit",
            CorrelationId = "corr-audit",
            CreatedAt = DateTimeOffset.UtcNow,
            LastMessageAt = DateTimeOffset.UtcNow,
        };
        RecordingThreadManager threadManager = new(mapping);
        RecordingAuditLogger auditLogger = new();
        SlackConnector connector = BuildConnector(queue, threadManager, auditLogger: auditLogger);

        MessengerMessage message = new(
            MessageId: "MSG-AUDIT",
            AgentId: "agent-audit",
            TaskId: "TASK-AUDIT",
            Content: "Hello human",
            MessageType: MessageType.StatusUpdate,
            CorrelationId: "corr-audit",
            Timestamp: DateTimeOffset.UtcNow);

        await connector.SendMessageAsync(message, CancellationToken.None);

        auditLogger.Entries.Should().HaveCount(1, "every successful enqueue writes exactly one connector-level audit row");
        SlackAuditEntry entry = auditLogger.Entries[0];
        entry.RequestType.Should().Be(SlackConnector.ConnectorSendRequestType);
        entry.Direction.Should().Be("outbound");
        entry.Outcome.Should().Be("success");
        entry.CorrelationId.Should().Be("corr-audit");
        entry.TaskId.Should().Be("TASK-AUDIT");
        entry.AgentId.Should().Be("agent-audit");
        entry.TeamId.Should().Be(TeamId);
        entry.ChannelId.Should().Be("C-AUDIT");
        entry.ThreadTs.Should().Be("1700000300.000300");
        entry.ResponsePayload.Should().NotBeNullOrWhiteSpace();
        entry.ResponsePayload.Should().Contain("Hello human");
    }

    [Fact]
    public async Task SendQuestionAsync_writes_connector_level_audit_row()
    {
        // Stage 8.1 iter-2 item 1: the AgentQuestion path also
        // emits a connector-level audit row -- parity with
        // SendMessageAsync so neither send shape is silently
        // omitted from the audit trail.
        RecordingOutboundQueue queue = new();
        SlackThreadMapping mapping = new()
        {
            TaskId = "TASK-Q-AUDIT",
            TeamId = TeamId,
            ChannelId = "C-Q-AUDIT",
            ThreadTs = "1700000400.000400",
        };
        RecordingThreadManager threadManager = new(mapping);
        RecordingAuditLogger auditLogger = new();
        SlackConnector connector = BuildConnector(queue, threadManager, auditLogger: auditLogger);

        AgentQuestion question = new(
            QuestionId: "Q-AUDIT",
            AgentId: "agent-q-audit",
            TaskId: "TASK-Q-AUDIT",
            Title: "Approve?",
            Body: "Confirm please.",
            Severity: "warning",
            AllowedActions: new[] { new HumanAction("approve", "Approve", "approve", false) },
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId: "corr-q-audit");

        await connector.SendQuestionAsync(question, CancellationToken.None);

        auditLogger.Entries.Should().HaveCount(1);
        SlackAuditEntry entry = auditLogger.Entries[0];
        entry.RequestType.Should().Be(SlackConnector.ConnectorSendRequestType);
        entry.CorrelationId.Should().Be("corr-q-audit");
        entry.TaskId.Should().Be("TASK-Q-AUDIT");
        entry.ResponsePayload.Should().Contain("Approve?");
    }

    [Fact]
    public void SlackConnectorComponents_rejects_null_dependencies()
    {
        // Stage 8.1 iter-2 item 1: the composition bundle pins the
        // five named subsystems (audit logger, inbound transport,
        // outbound transport, thread manager, inbound event buffer)
        // at compile time so a future DI regression that drops any
        // single one fails fast at component construction.
        ISlackAuditLogger audit = new RecordingAuditLogger();
        ISlackInboundQueue inbound = new StubInboundQueue();
        ISlackOutboundQueue outbound = new RecordingOutboundQueue();
        ISlackThreadManager thread = new RecordingThreadManager(null);
        ISlackInboundEventBuffer buffer = new InMemorySlackInboundEventBuffer();

        Action a = () => new SlackConnectorComponents(null!, inbound, outbound, thread, buffer);
        Action b = () => new SlackConnectorComponents(audit, null!, outbound, thread, buffer);
        Action c = () => new SlackConnectorComponents(audit, inbound, null!, thread, buffer);
        Action d = () => new SlackConnectorComponents(audit, inbound, outbound, null!, buffer);
        Action e = () => new SlackConnectorComponents(audit, inbound, outbound, thread, null!);

        a.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("auditLogger");
        b.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("inboundTransport");
        c.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("outboundTransport");
        d.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("threadManager");
        e.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("inboundEventBuffer");
    }

    private static SlackConnector BuildConnector(
        ISlackOutboundQueue queue,
        ISlackThreadManager threadManager,
        string? defaultTeamId = TeamId,
        ISlackInboundEventBuffer? buffer = null,
        RecordingAuditLogger? auditLogger = null)
    {
        SlackOutboundOptions opts = new() { DefaultTeamId = defaultTeamId };
        SlackConnectorComponents components = new(
            auditLogger: auditLogger ?? new RecordingAuditLogger(),
            inboundTransport: new StubInboundQueue(),
            outboundTransport: queue,
            threadManager: threadManager,
            inboundEventBuffer: buffer ?? new InMemorySlackInboundEventBuffer());

        return new SlackConnector(
            components,
            new StubRenderer(),
            new StaticOptionsMonitor<SlackOutboundOptions>(opts),
            NullLogger<SlackConnector>.Instance);
    }

    /// <summary>
    /// Stub renderer that wraps the source object so the connector
    /// test can assert that the rendered JSON contains the original
    /// content without depending on the default renderer's Block Kit
    /// shape.
    /// </summary>
    private sealed class StubRenderer : ISlackMessageRenderer
    {
        public object RenderReviewModal(SlackReviewModalContext context) => new { type = "modal" };

        public object RenderEscalateModal(SlackEscalateModalContext context) => new { type = "modal" };

        public object RenderCommentModal(SlackCommentModalContext context) => new { type = "modal" };

        public object RenderQuestion(AgentQuestion question) => new
        {
            attachments = new[]
            {
                new
                {
                    blocks = new object[]
                    {
                        new { type = "header", text = new { type = "plain_text", text = question.Title } },
                        new { type = "section", text = new { type = "mrkdwn", text = question.Body } },
                    },
                },
            },
        };

        public object RenderMessage(MessengerMessage message) => new
        {
            attachments = new[]
            {
                new
                {
                    blocks = new object[]
                    {
                        new { type = "section", text = new { type = "mrkdwn", text = message.Content } },
                    },
                },
            },
        };
    }

    internal sealed class RecordingOutboundQueue : ISlackOutboundQueue
    {
        private readonly List<SlackOutboundEnvelope> enqueued = new();

        public IReadOnlyList<SlackOutboundEnvelope> Enqueued => this.enqueued;

        public ValueTask EnqueueAsync(SlackOutboundEnvelope envelope)
        {
            this.enqueued.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public ValueTask<SlackOutboundEnvelope> DequeueAsync(CancellationToken ct)
        {
            throw new NotImplementedException("dispatcher tests dequeue from BlockingChannelOutboundQueue, not this stub");
        }
    }

    internal sealed class RecordingThreadManager : ISlackThreadManager
    {
        private readonly SlackThreadMapping? mapping;
        private readonly List<GetOrCreateCall> calls = new();

        public RecordingThreadManager(SlackThreadMapping? mapping)
        {
            this.mapping = mapping;
        }

        public IReadOnlyList<GetOrCreateCall> GetOrCreateCalls => this.calls;

        public Task<SlackThreadMapping> GetOrCreateThreadAsync(
            string taskId, string agentId, string correlationId, string teamId, CancellationToken ct)
        {
            this.calls.Add(new GetOrCreateCall(taskId, agentId, correlationId, teamId));
            if (this.mapping is null)
            {
                throw new InvalidOperationException("no mapping configured on RecordingThreadManager");
            }

            return Task.FromResult(this.mapping);
        }

        public Task<SlackThreadMapping?> GetThreadAsync(string taskId, CancellationToken ct)
            => Task.FromResult(this.mapping);

        public Task<bool> TouchAsync(string taskId, CancellationToken ct)
            => Task.FromResult(this.mapping is not null);

        public Task<SlackThreadMapping?> RecoverThreadAsync(
            string taskId, string agentId, string correlationId, string teamId, CancellationToken ct)
            => Task.FromResult(this.mapping);

        public Task<SlackThreadPostResult> PostThreadedReplyAsync(
            string taskId, string text, string? correlationId, CancellationToken ct)
            => Task.FromResult(this.mapping is not null
                ? SlackThreadPostResult.Posted(this.mapping, "1700000000.999999")
                : SlackThreadPostResult.MappingMissing(taskId));

        internal sealed record GetOrCreateCall(string TaskId, string AgentId, string CorrelationId, string TeamId);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T value;

        public StaticOptionsMonitor(T value)
        {
            this.value = value;
        }

        public T CurrentValue => this.value;

        public T Get(string? name) => this.value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    internal sealed class RecordingAuditLogger : ISlackAuditLogger
    {
        private readonly List<SlackAuditEntry> entries = new();

        public IReadOnlyList<SlackAuditEntry> Entries => this.entries;

        public Task LogAsync(SlackAuditEntry entry, CancellationToken ct)
        {
            this.entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SlackAuditEntry>> QueryAsync(SlackAuditQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SlackAuditEntry>>(this.entries);
    }

    internal sealed class StubInboundQueue : ISlackInboundQueue
    {
        public ValueTask EnqueueAsync(SlackInboundEnvelope envelope) => ValueTask.CompletedTask;

        public ValueTask<SlackInboundEnvelope> DequeueAsync(CancellationToken ct)
        {
            throw new NotImplementedException("SlackConnector unit tests never drain the inbound queue.");
        }
    }
}
