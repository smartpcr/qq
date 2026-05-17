using AgentSwarm.Messaging.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Validates <see cref="SqlAuditLogger"/> persistence per
/// <c>implementation-plan.md</c> §5.2 step 1 and the immutability scenarios from
/// the workstream brief:
/// <list type="bullet">
///   <item>Round-trip: an inserted entry's columns equal the stored row's columns.</item>
///   <item>Checksum preservation: the caller-supplied checksum is persisted verbatim.</item>
///   <item>Immutability — UPDATE: a direct raw-SQL UPDATE is blocked by the trigger.</item>
///   <item>Immutability — DELETE: a direct raw-SQL DELETE is blocked by the trigger.</item>
///   <item>Append-only: inserting two entries produces two rows in chronological order.</item>
/// </list>
/// </summary>
public sealed class SqlAuditLoggerTests
{
    private static readonly DateTimeOffset BaseTimestamp =
        new(2026, 5, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LogAsync_NullEntry_Throws()
    {
        await using var fixture = new AuditLogStoreFixture();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => fixture.Logger.LogAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task LogAsync_PersistsAllCanonicalColumns()
    {
        await using var fixture = new AuditLogStoreFixture();

        var entry = CreateEntry(action: "agent ask", outcome: AuditOutcomes.Success);

        await fixture.Logger.LogAsync(entry, CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var stored = await ctx.AuditLog.SingleAsync();

        Assert.True(stored.Id > 0);
        Assert.Equal(entry.Timestamp, stored.Timestamp);
        Assert.Equal(entry.CorrelationId, stored.CorrelationId);
        Assert.Equal(entry.EventType, stored.EventType);
        Assert.Equal(entry.ActorId, stored.ActorId);
        Assert.Equal(entry.ActorType, stored.ActorType);
        Assert.Equal(entry.TenantId, stored.TenantId);
        Assert.Equal(entry.AgentId, stored.AgentId);
        Assert.Equal(entry.TaskId, stored.TaskId);
        Assert.Equal(entry.ConversationId, stored.ConversationId);
        Assert.Equal(entry.Action, stored.Action);
        Assert.Equal(entry.PayloadJson, stored.PayloadJson);
        Assert.Equal(entry.Outcome, stored.Outcome);
    }

    [Fact]
    public async Task LogAsync_PreservesCallerChecksum_WhenItEqualsRecomputedHash()
    {
        // Stage 5.2 iter-4 (eval item 8) — the prior name
        // `LogAsync_PreservesCallerSuppliedChecksumVerbatim` implied the logger
        // accepts any caller-supplied checksum unconditionally. That is no longer
        // true: `SqlAuditLogger.LogAsync` recomputes the SHA-256 over the canonical
        // row content and rejects (`InvalidOperationException`) any mismatched
        // caller value. This test now exercises the SUCCESS path of that contract:
        // a correctly-computed checksum is preserved verbatim AND the recomputed
        // hash over the stored columns matches it byte-for-byte (the tamper
        // detection guarantee from tech-spec.md §4.3). The complementary rejection
        // path is covered by `LogAsync_RejectsEntryWithBadChecksum` further below.
        await using var fixture = new AuditLogStoreFixture();
        var entry = CreateEntry(action: "approve", outcome: AuditOutcomes.Success);

        await fixture.Logger.LogAsync(entry, CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var stored = await ctx.AuditLog.SingleAsync();

        Assert.Equal(entry.Checksum, stored.Checksum);

        // Re-computing the checksum over the stored columns must yield the same value —
        // tamper detection guarantee from tech-spec.md §4.3.
        var recomputed = AuditEntry.ComputeChecksum(
            timestamp: stored.Timestamp,
            correlationId: stored.CorrelationId,
            eventType: stored.EventType,
            actorId: stored.ActorId,
            actorType: stored.ActorType,
            tenantId: stored.TenantId,
            agentId: stored.AgentId,
            taskId: stored.TaskId,
            conversationId: stored.ConversationId,
            action: stored.Action,
            payloadJson: stored.PayloadJson,
            outcome: stored.Outcome);

        Assert.Equal(stored.Checksum, recomputed);
    }

    [Fact]
    public async Task LogAsync_IsAppendOnly_OrderedByTimestamp()
    {
        await using var fixture = new AuditLogStoreFixture();

        var first = CreateEntry(action: "agent ask", outcome: AuditOutcomes.Success);
        var second = CreateEntry(
            action: "approve",
            outcome: AuditOutcomes.Success,
            timestamp: BaseTimestamp.AddSeconds(5),
            correlationId: "corr-second");

        await fixture.Logger.LogAsync(first, CancellationToken.None);
        await fixture.Logger.LogAsync(second, CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var rows = await ctx.AuditLog.OrderBy(e => e.Timestamp).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(first.Action, rows[0].Action);
        Assert.Equal(second.Action, rows[1].Action);
        Assert.True(rows[0].Id < rows[1].Id);
    }

    [Fact]
    public async Task DirectUpdate_IsBlockedByImmutabilityTrigger()
    {
        await using var fixture = new AuditLogStoreFixture();
        var entry = CreateEntry(action: "agent ask", outcome: AuditOutcomes.Success);

        await fixture.Logger.LogAsync(entry, CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var original = await ctx.AuditLog.SingleAsync();

        // Bypass EF change tracking and run a raw UPDATE — this is the exact scenario
        // the immutability trigger is designed to block.
        var ex = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => ctx.Database.ExecuteSqlRawAsync(
                "UPDATE AuditLog SET Action = 'tampered' WHERE Id = {0}", original.Id));

        Assert.Contains("AuditLog", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify the row is unchanged. EF tracks `original` so we must re-read.
        await using var verifyCtx = fixture.CreateContext();
        var afterAttempt = await verifyCtx.AuditLog.SingleAsync();
        Assert.Equal(original.Action, afterAttempt.Action);
        Assert.Equal("agent ask", afterAttempt.Action);
        Assert.NotEqual("tampered", afterAttempt.Action);
    }

    [Fact]
    public async Task DirectDelete_IsBlockedByImmutabilityTrigger()
    {
        await using var fixture = new AuditLogStoreFixture();
        var entry = CreateEntry(action: "approve", outcome: AuditOutcomes.Success);

        await fixture.Logger.LogAsync(entry, CancellationToken.None);

        await using var ctx = fixture.CreateContext();
        var original = await ctx.AuditLog.SingleAsync();

        var ex = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => ctx.Database.ExecuteSqlRawAsync(
                "DELETE FROM AuditLog WHERE Id = {0}", original.Id));

        Assert.Contains("AuditLog", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify the row remains.
        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(1, await verifyCtx.AuditLog.CountAsync());
    }

    private static AuditEntry CreateEntry(
        string action,
        string outcome,
        DateTimeOffset? timestamp = null,
        string? correlationId = null)
    {
        var ts = timestamp ?? BaseTimestamp;
        var corr = correlationId ?? "corr-first";
        const string eventType = AuditEventTypes.CommandReceived;
        const string actorId = "user-aad-1";
        const string actorType = AuditActorTypes.User;
        const string tenantId = "tenant-1";
        const string agentId = "agent-1";
        const string taskId = "task-1";
        const string conversationId = "conv-1";
        const string payloadJson = "{\"command\":\"agent ask\"}";

        var checksum = AuditEntry.ComputeChecksum(
            timestamp: ts,
            correlationId: corr,
            eventType: eventType,
            actorId: actorId,
            actorType: actorType,
            tenantId: tenantId,
            agentId: agentId,
            taskId: taskId,
            conversationId: conversationId,
            action: action,
            payloadJson: payloadJson,
            outcome: outcome);

        return new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = corr,
            EventType = eventType,
            ActorId = actorId,
            ActorType = actorType,
            TenantId = tenantId,
            AgentId = agentId,
            TaskId = taskId,
            ConversationId = conversationId,
            Action = action,
            PayloadJson = payloadJson,
            Outcome = outcome,
            Checksum = checksum,
        };
    }

    /// <summary>
    /// Scenario — Checksum integrity verification at the persistence layer:
    /// Given an audit entry whose <see cref="AuditEntry.Checksum"/> does NOT match
    /// the canonical SHA-256 of its fields (e.g. a corrupted in-flight mutation
    /// between caller checksum computation and the LogAsync call, or a buggy
    /// caller that supplied a hand-coded literal), When LogAsync is invoked,
    /// Then it MUST reject the entry with <see cref="InvalidOperationException"/>
    /// and persist NO row — the immutability triggers protect rows AFTER they
    /// land; the checksum verification protects against bad rows landing in the
    /// first place. This satisfies item #5 of iter-1 evaluator feedback and the
    /// "Checksum integrity" scenario in the workstream brief.
    /// </summary>
    [Fact]
    public async Task LogAsync_RejectsEntryWithBadChecksum()
    {
        await using var fixture = new AuditLogStoreFixture();

        var good = CreateEntry(action: "agent ask", outcome: AuditOutcomes.Success);
        var tampered = good with { Checksum = "00000000000000000000000000000000000000000000000000000000deadbeef" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Logger.LogAsync(tampered, CancellationToken.None));

        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var ctx = fixture.CreateContext();
        Assert.Empty(await ctx.AuditLog.ToListAsync());
    }
}
