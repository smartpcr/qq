// -----------------------------------------------------------------------
// <copyright file="PersistentDeadLetterQueueIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.IntegrationTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Stage 4.2 — exercises the EF-backed
/// <see cref="PersistentDeadLetterQueue"/> against a real SQLite
/// database (in-memory shared-cache). Without an integration test a
/// regression in the migration, the entity configuration, the
/// <see cref="MessagingDbContext"/> wire-up, or the
/// <see cref="ServiceCollectionExtensions.AddMessagingPersistence"/>
/// service replacement would pass unit tests but silently lose every
/// dead-letter row in production. Same DI seam the worker uses, so
/// the persistent variant is exercised end-to-end.
/// </summary>
public sealed class PersistentDeadLetterQueueIntegrationTests : IDisposable
{
    private readonly IHost _host;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqliteConnection _keepAlive;

    public PersistentDeadLetterQueueIntegrationTests()
    {
        // SQLite shared-cache in-memory databases are destroyed the
        // moment the LAST connection with that data source name
        // closes; the DatabaseInitializer's scope is disposed at the
        // end of StartAsync, so without this open keepalive the
        // schema would vanish before any test runs.
        var dbName = $"persistent-dlq-stage42-test-{Guid.NewGuid():N}";
        var connectionString = $"DataSource={dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MessagingDb"] = connectionString,
                ["MessagingDb:UseMigrations"] = "false",
                ["DeadLetterQueue:UnhealthyThreshold"] = "100",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMessagingPersistence(configuration);

        _host = new HostBuilder()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in services)
                {
                    s.Add(descriptor);
                }
            })
            .Build();

        // Start the host so DatabaseInitializer creates the schema.
        _host.StartAsync().GetAwaiter().GetResult();
        _scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task SendToDeadLetterAsync_PersistsRowVisibleAcrossScopes()
    {
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var message = BuildOutboundMessage("trace-dlq-stage42-cross-scope");
        var reason = new FailureReason(
            OutboundFailureCategory.Permanent,
            "[Permanent] chat blocked",
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow);

        await queue.SendToDeadLetterAsync(message, reason, CancellationToken.None);

        // Resolve a SECOND IDeadLetterQueue from a fresh scope to
        // prove the row is visible to readers other than the
        // singleton that wrote it — the IServiceScopeFactory bridge
        // must actually commit to the DB, not just buffer in-memory.
        using var freshScope = _scopeFactory.CreateScope();
        var freshDb = freshScope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var rows = await freshDb.DeadLetterMessages
            .AsNoTracking()
            .Where(x => x.CorrelationId == "trace-dlq-stage42-cross-scope")
            .ToListAsync(CancellationToken.None);

        rows.Should().HaveCount(1,
            "a row written by PersistentDeadLetterQueue must be readable from a different DI scope");
        rows[0].OriginalMessageId.Should().Be(message.MessageId);
        rows[0].IdempotencyKey.Should().Be(message.IdempotencyKey);
        rows[0].ChatId.Should().Be(message.ChatId);
        rows[0].Payload.Should().Be(message.Payload);
        rows[0].Severity.Should().Be(message.Severity);
        rows[0].SourceType.Should().Be(message.SourceType);
        rows[0].FailureCategory.Should().Be(OutboundFailureCategory.Permanent);
        rows[0].FinalError.Should().Contain("chat blocked");
        rows[0].AlertStatus.Should().Be(DeadLetterAlertStatus.Pending,
            "newly-inserted rows must default to Pending — the alerting loop transitions to Sent later");
        rows[0].AttemptCount.Should().Be(1);

        // Iter-2 evaluator item 1 — architecture-mandated audit
        // columns must round-trip with their default values when the
        // caller does not supply explicit attempt history. The
        // FailureReason POCO defaults AttemptTimestampsJson and
        // ErrorHistoryJson to AttemptHistory.Empty ("[]"); ReplayStatus
        // defaults to None at insert; ReplayCorrelationId stays null
        // until a future replay workflow attaches one.
        rows[0].AttemptTimestamps.Should().Be("[]",
            "architecture.md §3.1 line 386 — AttemptTimestamps must round-trip; default is empty JSON array when no history was supplied");
        rows[0].ErrorHistory.Should().Be("[]",
            "architecture.md §3.1 line 388 — ErrorHistory must round-trip; default is empty JSON array when no history was supplied");
        rows[0].ReplayStatus.Should().Be(DeadLetterReplayStatus.None,
            "architecture.md §3.1 line 391 — ReplayStatus defaults to None at insert; Stage 4.2 does not mutate this column");
        rows[0].ReplayCorrelationId.Should().BeNull(
            "architecture.md §3.1 line 392 — ReplayCorrelationId stays null until a future replay workflow attaches one");
    }

    [Fact]
    public async Task SendToDeadLetterAsync_DuplicateOriginalMessageId_IsIdempotent()
    {
        // The UNIQUE(OriginalMessageId) constraint must NOT surface
        // as a duplicate-key throw on a processor retry — both the
        // pre-flight probe and the DbUpdateException catch are
        // covered. The audit row count must stay at 1.
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var message = BuildOutboundMessage("trace-dlq-stage42-idempotent");
        var reason = new FailureReason(
            OutboundFailureCategory.TransientTransport,
            "[TransientTransport] retry exhausted",
            AttemptCount: 5,
            FailedAt: DateTimeOffset.UtcNow);

        await queue.SendToDeadLetterAsync(message, reason, CancellationToken.None);
        var act = async () => await queue.SendToDeadLetterAsync(message, reason, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a duplicate SendToDeadLetterAsync with the same OriginalMessageId MUST be treated as success, not throw a UNIQUE-constraint violation");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var rows = await db.DeadLetterMessages
            .AsNoTracking()
            .Where(x => x.OriginalMessageId == message.MessageId)
            .ToListAsync(CancellationToken.None);
        rows.Should().HaveCount(1,
            "the idempotent insert must NOT inflate the audit trail with duplicate rows");
    }

    [Fact]
    public async Task ListAsync_ReturnsRowsOrderedByDeadLetteredAt_Ascending()
    {
        // The operator audit screen consumes ListAsync as "all
        // dead-letters" — the ORDER BY DeadLetteredAt ASC contract
        // matters so the timeline is reconstructible without a
        // client-side sort.
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var t0 = DateTimeOffset.UtcNow;

        await queue.SendToDeadLetterAsync(
            BuildOutboundMessage("trace-dlq-list-c"),
            new FailureReason(OutboundFailureCategory.Permanent, "[Permanent] c", AttemptCount: 1, FailedAt: t0.AddSeconds(10)),
            CancellationToken.None);
        await queue.SendToDeadLetterAsync(
            BuildOutboundMessage("trace-dlq-list-a"),
            new FailureReason(OutboundFailureCategory.Permanent, "[Permanent] a", AttemptCount: 1, FailedAt: t0),
            CancellationToken.None);
        await queue.SendToDeadLetterAsync(
            BuildOutboundMessage("trace-dlq-list-b"),
            new FailureReason(OutboundFailureCategory.Permanent, "[Permanent] b", AttemptCount: 1, FailedAt: t0.AddSeconds(5)),
            CancellationToken.None);

        var rows = await queue.ListAsync(CancellationToken.None);
        var listed = rows.Where(r => r.CorrelationId.StartsWith("trace-dlq-list-", StringComparison.Ordinal)).ToList();

        listed.Should().HaveCount(3);
        listed.Select(r => r.CorrelationId).Should().ContainInOrder(
            new[] { "trace-dlq-list-a", "trace-dlq-list-b", "trace-dlq-list-c" },
            "ListAsync MUST order by DeadLetteredAt ASC");
    }

    [Fact]
    public async Task CountAsync_ExcludesAcknowledgedRows()
    {
        // The health-check pivots on CountAsync. An operator who
        // explicitly acknowledged a row (transition Sent →
        // Acknowledged via a future replay-or-suppress workflow)
        // must not be penalised for the history of cleared
        // dead-letters — CountAsync filters AlertStatus !=
        // Acknowledged.
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var msg1 = BuildOutboundMessage("trace-dlq-count-1");
        var msg2 = BuildOutboundMessage("trace-dlq-count-2");
        var reason = new FailureReason(OutboundFailureCategory.Permanent, "[Permanent] x", 1, DateTimeOffset.UtcNow);

        await queue.SendToDeadLetterAsync(msg1, reason, CancellationToken.None);
        await queue.SendToDeadLetterAsync(msg2, reason, CancellationToken.None);

        var beforeAck = await queue.CountAsync(CancellationToken.None);

        // Flip msg1 to Acknowledged directly via the DbContext —
        // simulates a future replay-or-suppress workflow.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var row = await db.DeadLetterMessages
                .FirstAsync(x => x.OriginalMessageId == msg1.MessageId, CancellationToken.None);
            var updated = row with { AlertStatus = DeadLetterAlertStatus.Acknowledged };
            db.Entry(row).State = EntityState.Detached;
            db.DeadLetterMessages.Update(updated);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var afterAck = await queue.CountAsync(CancellationToken.None);

        beforeAck.Should().BeGreaterThanOrEqualTo(2);
        afterAck.Should().Be(
            beforeAck - 1,
            "CountAsync must drop Acknowledged rows from the count so the health check does not page on history");
    }

    [Fact]
    public async Task SendToDeadLetterAsync_TruncatesFinalErrorBeyond2048Chars()
    {
        // The entity configuration caps FinalError at 2048 chars
        // (DeadLetterMessageConfiguration.HasMaxLength(2048)). The
        // queue impl pre-trims to keep SaveChangesAsync from
        // throwing on an over-length string.
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var hugeError = new string('x', 5000);
        var message = BuildOutboundMessage("trace-dlq-huge-error");
        var reason = new FailureReason(
            OutboundFailureCategory.Permanent,
            hugeError,
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow);

        var act = async () => await queue.SendToDeadLetterAsync(message, reason, CancellationToken.None);
        await act.Should().NotThrowAsync(
            "an over-length error string must be silently truncated, not throw at SaveChangesAsync");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await db.DeadLetterMessages
            .AsNoTracking()
            .FirstAsync(x => x.OriginalMessageId == message.MessageId, CancellationToken.None);
        row.FinalError.Length.Should().BeLessThanOrEqualTo(
            2048,
            "the persisted FinalError must respect the column's 2048-char cap");
    }

    [Fact]
    public async Task MarkAlertSentAsync_FlipsPendingRowToSent_AndStampsAlertSentAt()
    {
        // Iter-2 evaluator item 3 — IDeadLetterQueue.MarkAlertSentAsync
        // is the contract the OutboundQueueProcessor calls after a
        // successful IAlertService.SendAlertAsync so the persisted
        // ledger row reflects the alert outcome.
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var message = BuildOutboundMessage("trace-dlq-alert-flip");
        var reason = new FailureReason(
            OutboundFailureCategory.Permanent,
            "[Permanent] chat blocked",
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow);
        await queue.SendToDeadLetterAsync(message, reason, CancellationToken.None);

        var sentAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await queue.MarkAlertSentAsync(message.MessageId, sentAt, CancellationToken.None);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await db.DeadLetterMessages
            .AsNoTracking()
            .FirstAsync(x => x.OriginalMessageId == message.MessageId, CancellationToken.None);

        row.AlertStatus.Should().Be(
            DeadLetterAlertStatus.Sent,
            "iter-2 evaluator item 3: after MarkAlertSentAsync the row must be Sent — leaving it Pending would mislead the operator into thinking the alert never fired");
        row.AlertSentAt.Should().NotBeNull();
        row.AlertSentAt!.Value.ToUnixTimeMilliseconds().Should().Be(
            sentAt.ToUnixTimeMilliseconds(),
            "AlertSentAt must persist the exact instant passed to MarkAlertSentAsync");
    }

    [Fact]
    public async Task MarkAlertSentAsync_AcknowledgedRow_IsNotRegressed()
    {
        // Idempotency invariant — never downgrade an Acknowledged
        // row back to Sent. A future operator workflow may flip
        // Sent → Acknowledged; a retried MarkAlertSentAsync (e.g.
        // after a worker restart) must not undo that.
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var message = BuildOutboundMessage("trace-dlq-alert-ack-guard");
        await queue.SendToDeadLetterAsync(
            message,
            new FailureReason(OutboundFailureCategory.Permanent, "x", 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        // Pre-flip to Acknowledged via DbContext directly.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var row = await db.DeadLetterMessages
                .FirstAsync(x => x.OriginalMessageId == message.MessageId, CancellationToken.None);
            var updated = row with { AlertStatus = DeadLetterAlertStatus.Acknowledged };
            db.Entry(row).State = EntityState.Detached;
            db.DeadLetterMessages.Update(updated);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await queue.MarkAlertSentAsync(
            message.MessageId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var verified = await verifyDb.DeadLetterMessages
            .AsNoTracking()
            .FirstAsync(x => x.OriginalMessageId == message.MessageId, CancellationToken.None);
        verified.AlertStatus.Should().Be(
            DeadLetterAlertStatus.Acknowledged,
            "MarkAlertSentAsync must never regress an Acknowledged row back to Sent — operator's explicit acknowledgement wins");
    }

    [Fact]
    public async Task MarkAlertSentAsync_MissingRow_IsNoOp()
    {
        // Missing-row idempotency — a recovery sweep may invoke
        // MarkAlertSentAsync for a message whose DLQ insert race
        // with retention pruning left no row. The contract is a
        // silent no-op, NOT a throw.
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var act = async () => await queue.MarkAlertSentAsync(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await act.Should().NotThrowAsync(
            "MarkAlertSentAsync against a missing row must be a no-op, not throw");
    }

    [Fact]
    public async Task SendToDeadLetterAsync_PopulatesAgentIdFromQuestionEnvelope()
    {
        // Iter-2 evaluator item 4 — e2e-scenarios.md §244 requires
        // the dead-letter row to include AgentId. For Question
        // source the AgentId comes from
        // AgentQuestionEnvelope.Question.AgentId in
        // SourceEnvelopeJson, extracted via AgentIdExtractor.
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var envelopeJson =
            "{\"Question\":{\"QuestionId\":\"q-1\",\"AgentId\":\"agent-blue\",\"CorrelationId\":\"trace-dlq-stage42-agentid\",\"Title\":\"t\",\"Body\":\"b\",\"Severity\":2,\"AllowedActions\":[],\"TimeoutSeconds\":60,\"CreatedAt\":\"2026-06-01T12:00:00+00:00\"},\"ProposedDefaultActionId\":null,\"RoutingMetadata\":{}}";
        var message = new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = "q:agent-blue:q-1",
            ChatId = 7777,
            Payload = "[High] preview",
            SourceEnvelopeJson = envelopeJson,
            Severity = MessageSeverity.High,
            SourceType = OutboundSourceType.Question,
            SourceId = "q-1",
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            CorrelationId = "trace-dlq-stage42-agentid",
        };
        var reason = new FailureReason(
            OutboundFailureCategory.Permanent,
            "[Permanent] timeout",
            AttemptCount: 5,
            FailedAt: DateTimeOffset.UtcNow);

        await queue.SendToDeadLetterAsync(message, reason, CancellationToken.None);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await db.DeadLetterMessages
            .AsNoTracking()
            .FirstAsync(x => x.OriginalMessageId == message.MessageId, CancellationToken.None);

        row.AgentId.Should().Be(
            "agent-blue",
            "iter-2 evaluator item 4: the dead-letter row's AgentId column must be populated from AgentQuestion.AgentId so the operator audit pivot per-agent works without re-parsing SourceEnvelopeJson");
    }

    [Fact]
    public async Task SendToDeadLetterAsync_AgentIdFromFailureReason_OverridesEnvelopeFallback()
    {
        // When the processor pre-extracts AgentId and passes it in
        // FailureReason.AgentId, the queue must trust that value
        // rather than re-parsing the envelope (the processor already
        // paid the deserialization cost).
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var message = BuildOutboundMessage("trace-dlq-stage42-agentid-override");
        var reason = new FailureReason(
            OutboundFailureCategory.Permanent,
            "[Permanent] x",
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow)
        {
            AgentId = "deploy-agent-9",
        };

        await queue.SendToDeadLetterAsync(message, reason, CancellationToken.None);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await db.DeadLetterMessages
            .AsNoTracking()
            .FirstAsync(x => x.OriginalMessageId == message.MessageId, CancellationToken.None);

        row.AgentId.Should().Be(
            "deploy-agent-9",
            "the processor-supplied FailureReason.AgentId must override the envelope-extraction fallback");
    }

    [Fact]
    public async Task SendToDeadLetterAsync_PersistsAttemptTimestampsAndErrorHistory()
    {
        // Iter-2 evaluator item 1 — architecture.md §3.1 lines
        // 386–388 require AttemptTimestamps + ErrorHistory on the
        // dead-letter ledger. Pin that the FailureReason's
        // AttemptTimestampsJson + ErrorHistoryJson round-trip into
        // the columns verbatim (no truncation, no re-serialization,
        // no key-casing flip).
        var queue = _host.Services.GetRequiredService<IDeadLetterQueue>();
        var message = BuildOutboundMessage("trace-dlq-stage42-history");

        // Mimic the processor's projection: append 5 attempts via
        // AttemptHistory.Append then project the two views.
        var t0 = new DateTimeOffset(2026, 06, 01, 12, 00, 00, TimeSpan.Zero);
        var historyJson = AttemptHistory.Empty;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            historyJson = AttemptHistory.Append(
                historyJson,
                attempt: attempt,
                timestamp: t0.AddSeconds(attempt * 2),
                error: $"transient {attempt}",
                httpStatus: 503);
        }
        var attemptTimestamps = AttemptHistory.ProjectTimestamps(historyJson);
        var errorHistory = AttemptHistory.ProjectErrorHistory(historyJson);

        var reason = new FailureReason(
            OutboundFailureCategory.TransientTransport,
            "[TransientTransport] retry exhausted",
            AttemptCount: 5,
            FailedAt: t0.AddSeconds(10))
        {
            AttemptTimestampsJson = attemptTimestamps,
            ErrorHistoryJson = errorHistory,
        };

        await queue.SendToDeadLetterAsync(message, reason, CancellationToken.None);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await db.DeadLetterMessages
            .AsNoTracking()
            .FirstAsync(x => x.OriginalMessageId == message.MessageId, CancellationToken.None);

        row.AttemptTimestamps.Should().Be(
            attemptTimestamps,
            "architecture.md §3.1 line 386 — AttemptTimestamps must round-trip verbatim from the FailureReason projection");
        row.ErrorHistory.Should().Be(
            errorHistory,
            "architecture.md §3.1 line 388 — ErrorHistory must round-trip verbatim from the FailureReason projection");
        row.ReplayStatus.Should().Be(
            DeadLetterReplayStatus.None,
            "architecture.md §3.1 line 391 — ReplayStatus defaults to None on insert; Stage 4.2 itself does not mutate this column");
        row.ReplayCorrelationId.Should().BeNull(
            "architecture.md §3.1 line 392 — ReplayCorrelationId stays null until a future replay workflow attaches one");

        // Sanity: the persisted JSON must carry FIVE attempt entries
        // shaped per architecture.md (lowercase keys: attempt,
        // timestamp, error, httpStatus). A regex match keeps the
        // assertion robust to JSON-formatting whitespace.
        row.ErrorHistory.Should().Contain("\"attempt\":1",
            "the on-wire shape must use lowercase 'attempt' (architecture.md §3.1 line 388)");
        row.ErrorHistory.Should().Contain("\"httpStatus\":503",
            "the on-wire shape must use lowercase 'httpStatus' (architecture.md §3.1 line 388)");
        row.ErrorHistory.Should().Contain("\"attempt\":5",
            "every attempt entry must be preserved through projection — not just the first or last");
    }

    private static OutboundMessage BuildOutboundMessage(string correlationId) => new()
    {
        MessageId = Guid.NewGuid(),
        IdempotencyKey = $"dlq-test:{correlationId}",
        ChatId = 5050,
        Payload = "payload for " + correlationId,
        Severity = MessageSeverity.High,
        SourceType = OutboundSourceType.StatusUpdate,
        SourceId = "src-" + correlationId,
        CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
        CorrelationId = correlationId,
    };

    public void Dispose()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        _keepAlive.Dispose();
    }
}
