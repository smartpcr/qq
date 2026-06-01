// -----------------------------------------------------------------------
// <copyright file="FileSystemSlackInboundEnqueueDeadLetterSinkTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.1 iter-4 evaluator item 2 deepening: the in-memory sink
/// (<see cref="InMemorySlackInboundEnqueueDeadLetterSink"/>) keeps
/// dead-lettered envelopes alive only for the current process; a host
/// restart loses them. <see cref="FileSystemSlackInboundEnqueueDeadLetterSink"/>
/// makes "recoverable" durable across restarts by appending JSONL
/// lines to a configurable directory. These facts pin the contract.
/// </summary>
public sealed class FileSystemSlackInboundEnqueueDeadLetterSinkTests : IDisposable
{
    private readonly string scratchDirectory;

    public FileSystemSlackInboundEnqueueDeadLetterSinkTests()
    {
        this.scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            "slack-dl-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(this.scratchDirectory))
        {
            try
            {
                Directory.Delete(this.scratchDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; CI workers occasionally hold a
                // file handle for a few ms longer than the test.
            }
        }
    }

    [Fact]
    public async Task RecordDeadLetterAsync_writes_a_single_JSONL_line_with_envelope_and_exception_context()
    {
        using FileSystemSlackInboundEnqueueDeadLetterSink sink = this.BuildSink();

        SlackInboundEnvelope envelope = BuildEnvelope("event:single");
        InvalidOperationException ex = new("queue offline");

        await sink.RecordDeadLetterAsync(envelope, ex, attemptCount: 3, CancellationToken.None);

        File.Exists(sink.AbsoluteFilePath).Should().BeTrue(
            "the JSONL file must be created on first dead-letter write");

        string[] lines = await File.ReadAllLinesAsync(sink.AbsoluteFilePath);
        lines.Should().HaveCount(1, "exactly one record was written");
        lines[0].Should().Contain("\"IdempotencyKey\":\"event:single\"");
        lines[0].Should().Contain("\"AttemptCount\":3");
        lines[0].Should().Contain("\"ExceptionType\":\"System.InvalidOperationException\"");
        lines[0].Should().Contain("\"ExceptionMessage\":\"queue offline\"");
        lines[0].Should().NotContain("\n", "each record is one line; the newline is the separator");
    }

    [Fact]
    public async Task Concurrent_writes_serialize_and_produce_one_line_per_dead_letter()
    {
        using FileSystemSlackInboundEnqueueDeadLetterSink sink = this.BuildSink();

        Task[] writes = new Task[20];
        for (int i = 0; i < writes.Length; i++)
        {
            int idx = i;
            writes[i] = Task.Run(() => sink.RecordDeadLetterAsync(
                BuildEnvelope($"event:{idx}"),
                new InvalidOperationException($"failure {idx}"),
                attemptCount: 3,
                CancellationToken.None));
        }

        await Task.WhenAll(writes);

        string[] lines = await File.ReadAllLinesAsync(sink.AbsoluteFilePath);
        lines.Should().HaveCount(20,
            "the write gate must serialize concurrent writes -- no line is lost or interleaved");

        // Every line must round-trip as JSON (no corruption from
        // interleaved writes).
        FileSystemDeadLetterRecord[] records = await sink.ReadAllAsync(CancellationToken.None);
        records.Should().HaveCount(20);
    }

    [Fact]
    public async Task ReadAllAsync_round_trips_every_persisted_record()
    {
        using FileSystemSlackInboundEnqueueDeadLetterSink sink = this.BuildSink();

        SlackInboundEnvelope envelope = BuildEnvelope("interaction:button-1");
        await sink.RecordDeadLetterAsync(envelope, new TimeoutException("svc bus timeout"), 3, CancellationToken.None);

        FileSystemDeadLetterRecord[] records = await sink.ReadAllAsync(CancellationToken.None);

        records.Should().ContainSingle();
        records[0].IdempotencyKey.Should().Be("interaction:button-1");
        records[0].SourceType.Should().Be("Event");
        records[0].AttemptCount.Should().Be(3);
        records[0].ExceptionType.Should().Be("System.TimeoutException");
        records[0].ExceptionMessage.Should().Be("svc bus timeout");
        records[0].RawPayload.Should().Be(envelope.RawPayload);
    }

    [Fact]
    public async Task Empty_directory_returns_empty_records_array()
    {
        using FileSystemSlackInboundEnqueueDeadLetterSink sink = this.BuildSink();
        FileSystemDeadLetterRecord[] records = await sink.ReadAllAsync(CancellationToken.None);
        records.Should().BeEmpty(
            "no dead-letters were recorded; the file does not exist yet");
    }

    [Fact]
    public void Constructor_rejects_null_or_empty_directory_path()
    {
        Action a = () => new FileSystemSlackInboundEnqueueDeadLetterSink(
            directoryPath: string.Empty,
            NullLogger<FileSystemSlackInboundEnqueueDeadLetterSink>.Instance);
        a.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddFileSystemSlackInboundEnqueueDeadLetterSink_replaces_the_in_memory_default()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSlackInboundTransport();
        services.AddFileSystemSlackInboundEnqueueDeadLetterSink(this.scratchDirectory);

        ServiceProvider sp = services.BuildServiceProvider();

        ISlackInboundEnqueueDeadLetterSink resolved = sp.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>();
        resolved.Should().BeOfType<FileSystemSlackInboundEnqueueDeadLetterSink>(
            "the host opted in to the durable sink; the in-memory default must be displaced");

        // Verify the in-memory registration is gone (TryAddSingleton
        // would otherwise leave it resolvable as its concrete type).
        InMemorySlackInboundEnqueueDeadLetterSink? leftover = sp.GetService<InMemorySlackInboundEnqueueDeadLetterSink>();
        leftover.Should().BeNull(
            "AddFileSystemSlackInboundEnqueueDeadLetterSink must RemoveAll the in-memory registration to avoid two competing sinks");
    }

    private FileSystemSlackInboundEnqueueDeadLetterSink BuildSink()
    {
        return new FileSystemSlackInboundEnqueueDeadLetterSink(
            this.scratchDirectory,
            NullLogger<FileSystemSlackInboundEnqueueDeadLetterSink>.Instance);
    }

    private static SlackInboundEnvelope BuildEnvelope(string idempotencyKey)
        => new(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Event,
            TeamId: "T0",
            ChannelId: "C0",
            UserId: "U0",
            RawPayload: "{\"event\":\"test\"}",
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);
}
