// -----------------------------------------------------------------------
// <copyright file="FileAuditFallbackSinkTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Stage 5.3 iter-9 evaluator item 2 — pins the durable fallback
/// behaviour of <see cref="FileAuditFallbackSink"/>. The fallback
/// is the backstop for audit-DB outages; without these tests an
/// iter-N+1 refactor could regress the file format or the
/// flush-before-return contract and silently lose audit rows during
/// the next real audit-DB outage.
/// </summary>
public sealed class FileAuditFallbackSinkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public FileAuditFallbackSinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "audit-fallback-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "audit-fallback.jsonl");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup; the OS will reclaim the temp dir
        }
    }

    [Fact]
    public async Task EnqueueAsync_General_WritesOneJsonLine_WithAllRequiredFields()
    {
        var sink = NewSink();
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "msg-42",
            UserId = "u-7",
            AgentId = "agent-9",
            Action = "command.denied",
            EventFamily = AuditEventFamilies.Lifecycle,
            Timestamp = new DateTimeOffset(2026, 5, 17, 12, 34, 56, TimeSpan.Zero),
            CorrelationId = "trace-fb-1",
            TenantId = "tenant-a",
            Details = "{\"phase\":\"parse-empty\"}",
        };

        await sink.EnqueueAsync(entry, default);

        var lines = await File.ReadAllLinesAsync(_filePath);
        lines.Should().HaveCount(1, "exactly one EnqueueAsync call wrote exactly one line");

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be(FileAuditFallbackSink.TypeGeneral);
        root.GetProperty("entryId").GetGuid().Should().Be(entry.EntryId);
        root.GetProperty("messageId").GetString().Should().Be("msg-42");
        root.GetProperty("userId").GetString().Should().Be("u-7");
        root.GetProperty("agentId").GetString().Should().Be("agent-9");
        root.GetProperty("action").GetString().Should().Be("command.denied");
        root.GetProperty("eventFamily").GetString().Should().Be(AuditEventFamilies.Lifecycle);
        root.GetProperty("timestamp").GetInt64().Should().Be(entry.Timestamp.ToUnixTimeMilliseconds());
        root.GetProperty("correlationId").GetString().Should().Be("trace-fb-1");
        root.GetProperty("tenantId").GetString().Should().Be("tenant-a");
        root.GetProperty("details").GetString().Should().Be("{\"phase\":\"parse-empty\"}");
    }

    [Fact]
    public async Task EnqueueAsync_HumanResponse_WritesOneJsonLine_WithRequiredFields()
    {
        var sink = NewSink();
        var entry = new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "callback-1",
            UserId = "u-7",
            AgentId = "agent-9",
            QuestionId = "q-100",
            ActionValue = "approve",
            Comment = "looks good",
            Timestamp = new DateTimeOffset(2026, 5, 17, 12, 34, 56, TimeSpan.Zero),
            CorrelationId = "trace-fb-hr",
            TenantId = "tenant-a",
            Details = "{\"source\":\"callback\"}",
        };

        await sink.EnqueueAsync(entry, default);

        var lines = await File.ReadAllLinesAsync(_filePath);
        lines.Should().HaveCount(1);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be(FileAuditFallbackSink.TypeHumanResponse);
        root.GetProperty("entryId").GetGuid().Should().Be(entry.EntryId);
        root.GetProperty("questionId").GetString().Should().Be("q-100");
        root.GetProperty("actionValue").GetString().Should().Be("approve");
        root.GetProperty("comment").GetString().Should().Be("looks good");
    }

    [Fact]
    public async Task EnqueueAsync_AppendsMultipleLines_WithoutInterleaving()
    {
        var sink = NewSink();

        // 100 concurrent writes; each line is a complete JSON
        // document with a unique entryId so we can detect both byte
        // interleaving (parse failure) AND row loss (count
        // mismatch).
        const int count = 100;
        var entries = Enumerable.Range(0, count)
            .Select(i => new AuditEntry
            {
                EntryId = Guid.NewGuid(),
                UserId = $"u-{i}",
                Action = "command.denied",
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = $"trace-{i}",
            })
            .ToArray();

        await Task.WhenAll(entries.Select(e => sink.EnqueueAsync(e, default)));

        var lines = await File.ReadAllLinesAsync(_filePath);
        lines.Should().HaveCount(count, "the serialisation lock must serialise concurrent writes so no row is dropped");

        var observedIds = lines
            .Select(l =>
            {
                using var doc = JsonDocument.Parse(l);
                return doc.RootElement.GetProperty("entryId").GetGuid();
            })
            .ToHashSet();
        observedIds.Should().BeEquivalentTo(entries.Select(e => e.EntryId),
            "every entry id MUST appear in the file exactly once — no interleaving and no loss");
    }

    [Fact]
    public async Task EnqueueAsync_CreatesParentDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempDir, "deep", "nested", "audit-fallback.jsonl");
        var sink = new FileAuditFallbackSink(
            nested,
            NullLogger<FileAuditFallbackSink>.Instance);

        await sink.EnqueueAsync(NewEntry(), default);

        File.Exists(nested).Should().BeTrue(
            "the constructor MUST create any missing parent directory so a fresh deploy works without manual mkdir");
    }

    [Fact]
    public void Constructor_RejectsNullOrWhitespacePath()
    {
        Action act = () => new FileAuditFallbackSink("   ", NullLogger<FileAuditFallbackSink>.Instance);
        act.Should().Throw<ArgumentException>("a fallback sink MUST have a real path or the durability contract is violated");
    }

    [Fact]
    public void FilePath_IsAbsolutePath()
    {
        // Sanity: support / forensic tooling needs the absolute path
        // to tail the file. Path.GetFullPath normalisation matters
        // because a relative path passed at construction time would
        // otherwise resolve relative to whichever scope happens to
        // be active at write time (and could move).
        var sink = NewSink();
        Path.IsPathRooted(sink.FilePath).Should().BeTrue();
    }

    private FileAuditFallbackSink NewSink() =>
        new(_filePath, NullLogger<FileAuditFallbackSink>.Instance);

    private static AuditEntry NewEntry() => new()
    {
        EntryId = Guid.NewGuid(),
        UserId = "u-x",
        Action = "command.denied",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = "trace-x",
    };
}
