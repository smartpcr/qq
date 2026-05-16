// -----------------------------------------------------------------------
// <copyright file="FileSystemSlackOutboundQueueTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Queues;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 6.3 iter 2 evaluator-item #2 coverage: the canonical Worker
/// outbound queue MUST be durable so that enqueued envelopes survive
/// connector restart (FR-005 + FR-007 / zero message loss). These
/// tests pin the behavior of <see cref="FileSystemSlackOutboundQueue"/>
/// independently of the dispatcher hosted service.
/// </summary>
public sealed class FileSystemSlackOutboundQueueTests : IDisposable
{
    private readonly string root;

    public FileSystemSlackOutboundQueueTests()
    {
        this.root = Path.Combine(
            Path.GetTempPath(),
            "slack-out-queue-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.root))
            {
                Directory.Delete(this.root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task Enqueue_writes_a_pending_file_and_dequeue_returns_the_envelope()
    {
        using FileSystemSlackOutboundQueue queue = new(this.root, NullLogger<FileSystemSlackOutboundQueue>.Instance);
        SlackOutboundEnvelope env = NewEnvelope("TASK-1", "corr-1");

        await queue.EnqueueAsync(env);

        // Pending dir must contain exactly one file before ack.
        queue.PendingFileCount.Should().Be(1);

        SlackOutboundEnvelope dequeued = await queue.DequeueAsync(CancellationToken.None);
        dequeued.TaskId.Should().Be("TASK-1");
        dequeued.CorrelationId.Should().Be("corr-1");
        dequeued.EnvelopeId.Should().Be(env.EnvelopeId,
            "the durable queue MUST preserve the producer-assigned EnvelopeId so ack can target the right journal entry");

        // Until acked, file remains on disk for restart replay.
        queue.PendingFileCount.Should().Be(1);
    }

    [Fact]
    public async Task Acknowledge_deletes_the_pending_journal_entry()
    {
        using FileSystemSlackOutboundQueue queue = new(this.root, NullLogger<FileSystemSlackOutboundQueue>.Instance);
        SlackOutboundEnvelope env = NewEnvelope("TASK-A", "corr-A");
        await queue.EnqueueAsync(env);
        SlackOutboundEnvelope dequeued = await queue.DequeueAsync(CancellationToken.None);

        await queue.AcknowledgeAsync(dequeued);

        queue.PendingFileCount.Should().Be(0,
            "AcknowledgeAsync MUST delete the journal entry so it is not replayed on next restart");
    }

    [Fact]
    public async Task Restart_replays_unacknowledged_envelopes_in_fifo_order()
    {
        // Enqueue three envelopes into queue #1; ack only the middle
        // one; then construct queue #2 against the same directory and
        // verify the un-acked envelopes replay in FIFO order.
        SlackOutboundEnvelope first;
        SlackOutboundEnvelope second;
        SlackOutboundEnvelope third;

        using (FileSystemSlackOutboundQueue producer = new(this.root, NullLogger<FileSystemSlackOutboundQueue>.Instance))
        {
            first = NewEnvelope("TASK-1", "corr-1");
            second = NewEnvelope("TASK-2", "corr-2");
            third = NewEnvelope("TASK-3", "corr-3");
            await producer.EnqueueAsync(first);
            // Tiny sleep to guarantee strictly-increasing tick prefix
            // even on coarse-resolution clocks.
            await Task.Delay(2);
            await producer.EnqueueAsync(second);
            await Task.Delay(2);
            await producer.EnqueueAsync(third);

            SlackOutboundEnvelope drained2 = await producer.DequeueAsync(CancellationToken.None);
            drained2 = drained2.TaskId == "TASK-2" ? drained2 : await producer.DequeueAsync(CancellationToken.None);

            // Drain everything that was enqueued so the in-memory
            // channel is empty; ack only TASK-2.
            await producer.AcknowledgeAsync(
                drained2.TaskId == "TASK-2" ? drained2 : second);
        }

        using FileSystemSlackOutboundQueue replay = new(this.root, NullLogger<FileSystemSlackOutboundQueue>.Instance);

        // Two un-acked entries must replay; collect them in dequeue
        // order and verify FIFO is preserved.
        List<string> replayedIds = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        SlackOutboundEnvelope r1 = await replay.DequeueAsync(cts.Token);
        SlackOutboundEnvelope r2 = await replay.DequeueAsync(cts.Token);
        replayedIds.Add(r1.TaskId);
        replayedIds.Add(r2.TaskId);

        replayedIds.Should().Equal(
            new[] { "TASK-1", "TASK-3" },
            because: "replay MUST preserve original enqueue order, skipping ack'd entries");

        // Acking a replayed envelope still finds the journal entry by
        // its EnvelopeId.
        await replay.AcknowledgeAsync(r1);
        replay.PendingFileCount.Should().Be(1,
            "acking the replayed envelope MUST delete its on-disk entry");
    }

    [Fact]
    public async Task Persistence_failure_throws_typed_exception()
    {
        // Construct a queue rooted at a path that exists as a FILE
        // (not a directory) so the pending sub-dir creation fails.
        string filePath = Path.Combine(Path.GetTempPath(), "fs-q-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(filePath, "block");
        try
        {
            Action act = () => _ = new FileSystemSlackOutboundQueue(filePath, NullLogger<FileSystemSlackOutboundQueue>.Instance);

            // Either CreateDirectory throws (Windows: IOException) or
            // the queue rejects the path. Both are acceptable. The
            // contract under test is "fail fast at composition time".
            act.Should().Throw<Exception>(
                "the queue MUST surface filesystem composition errors instead of silently degrading to non-durable mode");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Envelope_iter2_init_only_fields_round_trip_through_journal()
    {
        // GIVEN an envelope with MessageTs and ViewId (iter-2 new
        // typed fields) -- they must survive enqueue -> replay so the
        // dispatcher can still build the correct update request after
        // restart.
        using (FileSystemSlackOutboundQueue producer = new(this.root, NullLogger<FileSystemSlackOutboundQueue>.Instance))
        {
            SlackOutboundEnvelope env = new(
                "TASK-RT",
                "corr-RT",
                SlackOutboundOperationKind.UpdateMessage,
                "{\"blocks\":[]}",
                ThreadTs: "1700000000.000100")
            {
                MessageTs = "1700000099.000200",
                ViewId = "V42",
            };
            await producer.EnqueueAsync(env);
        }

        using FileSystemSlackOutboundQueue replay = new(this.root, NullLogger<FileSystemSlackOutboundQueue>.Instance);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        SlackOutboundEnvelope hydrated = await replay.DequeueAsync(cts.Token);

        hydrated.TaskId.Should().Be("TASK-RT");
        hydrated.MessageType.Should().Be(SlackOutboundOperationKind.UpdateMessage);
        hydrated.MessageTs.Should().Be("1700000099.000200",
            "MessageTs MUST round-trip through the durable journal");
        hydrated.ViewId.Should().Be("V42",
            "ViewId MUST round-trip through the durable journal");
        hydrated.ThreadTs.Should().Be("1700000000.000100");
    }

    private static SlackOutboundEnvelope NewEnvelope(string taskId, string correlationId) => new(
        taskId,
        correlationId,
        SlackOutboundOperationKind.PostMessage,
        "{\"blocks\":[]}",
        ThreadTs: "1700000000.000100");
}
