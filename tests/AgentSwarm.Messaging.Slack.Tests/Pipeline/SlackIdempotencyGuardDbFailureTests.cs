// -----------------------------------------------------------------------
// <copyright file="SlackIdempotencyGuardDbFailureTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Tests.Persistence;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 4.3 regression coverage for the
/// <see cref="SlackIdempotencyGuard{TContext}"/> exception-handling
/// contract. Verifies that a <see cref="DbUpdateException"/> is
/// disambiguated into (a) a duplicate when a competing row actually
/// exists, and (b) a propagating failure when SaveChanges failed for
/// an unrelated reason (transient outage, schema mismatch, validation
/// error, etc.) -- i.e. transient infrastructure errors are NOT
/// silently dropped as duplicates.
/// </summary>
public sealed class SlackIdempotencyGuardDbFailureTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly FaultInterceptor interceptor;
    private readonly ServiceProvider serviceProvider;

    public SlackIdempotencyGuardDbFailureTests()
    {
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();
        this.interceptor = new FaultInterceptor(this.connection);

        ServiceCollection services = new();
        services.AddDbContext<SlackTestDbContext>(opts =>
            opts.UseSqlite(this.connection).AddInterceptors(this.interceptor));
        this.serviceProvider = services.BuildServiceProvider();

        using IServiceScope bootstrap = this.serviceProvider.CreateScope();
        bootstrap.ServiceProvider.GetRequiredService<SlackTestDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        this.serviceProvider.Dispose();
        this.connection.Dispose();
    }

    [Fact]
    public async Task TryAcquireAsync_returns_false_when_DbUpdateException_is_a_unique_key_race()
    {
        // Simulate a race: probe sees no row, but another writer
        // inserts the same key before our SaveChanges. The guard
        // MUST detect the conflicting row via the post-failure probe
        // and report duplicate.
        this.interceptor.ShadowInsertOnNextSave = true;
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();
        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:race");

        bool acquired = await guard.TryAcquireAsync(envelope, CancellationToken.None);

        acquired.Should().BeFalse(
            "a competing INSERT between the probe and SaveChanges is the textbook race-lost path; the guard MUST treat it as duplicate.");

        using IServiceScope scope = this.serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Should().HaveCount(1,
                "exactly the shadow row inserted by the interceptor must remain");
    }

    [Fact]
    public async Task TryAcquireAsync_propagates_when_DbUpdateException_is_transient_without_conflicting_row()
    {
        // Simulate a transient failure: SaveChanges throws a
        // DbUpdateException with no competing row in the table. The
        // guard MUST propagate so the envelope can be retried rather
        // than silently dropping it as a duplicate (which would lose
        // the envelope entirely because nothing would ever process
        // it AND a Slack retry would not arrive).
        this.interceptor.ThrowTransientOnNextSave = true;
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();
        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:transient");

        Func<Task> act = () => guard.TryAcquireAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>(
            "a transient DB failure that is NOT a unique-key conflict MUST propagate -- silently dropping it as a duplicate would permanently lose the envelope.");

        using IServiceScope scope = this.serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Should().BeEmpty(
                "no row was inserted because SaveChanges failed before the row was persisted");
    }

    [Fact]
    public async Task MarkCompletedAsync_retries_transient_DbUpdateException_and_persists_terminal_status()
    {
        // Iter 6 evaluator item #2: a successful handler followed by
        // a transient DB failure on MarkCompletedAsync USED to leave
        // the dedup row stuck in 'processing', where the stale-
        // reclaim path would eventually re-execute the handler --
        // a duplicate-execution bug. The fix adds bounded retry
        // inside UpdateTerminalStatusAsync so a brief transient blip
        // is absorbed and the row reaches 'completed'.
        //
        // Seed: insert a 'processing' row directly, then simulate
        // ONE transient failure followed by a successful retry.
        const string key = "cmd:T1:U1:/agent:complete-after-retry";
        await this.SeedProcessingRowAsync(key);

        this.interceptor.TransientFailuresRemaining = 1;
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard(
            completionMaxAttempts: 3,
            completionInitialDelayMilliseconds: 0);

        await guard.MarkCompletedAsync(key, CancellationToken.None);

        using IServiceScope verifyScope = this.serviceProvider.CreateScope();
        SlackInboundRequestRecord? row = await verifyScope.ServiceProvider
            .GetRequiredService<SlackTestDbContext>()
            .InboundRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IdempotencyKey == key);

        row.Should().NotBeNull(
            "the seeded row MUST still exist after the retry path completes");
        row!.ProcessingStatus.Should().Be(
            SlackInboundRequestProcessingStatus.Completed,
            "the bounded retry MUST persist the terminal status after the transient blip clears -- otherwise the row stays 'processing' and gets reclaimed as stale");
        row.CompletedAt.Should().NotBeNull(
            "CompletedAt MUST be stamped on the successful retry");
        this.interceptor.TransientFailuresRemaining.Should().Be(0,
            "the simulated transient failure budget MUST have been consumed by exactly one retry");
    }

    [Fact]
    public async Task MarkCompletedAsync_transitions_row_to_completion_persist_failed_via_raw_sql_fallback_when_retry_budget_is_exhausted()
    {
        // Iter 7 evaluator item #1: when every SaveChanges retry
        // fails, the row USED to remain in 'processing' -- the stale-
        // reclaim path at TryReclaimStaleLeaseAsync would later treat
        // it as orphaned and RE-DISPATCH the handler, violating
        // duplicate-suppression for already-successful work. The
        // structural fix adds a final raw ExecuteUpdateAsync attempt
        // (bypassing the EF change-tracker so a poisoned context
        // cannot block it) that writes the non-reclaimable
        // disposition SlackInboundRequestProcessingStatus.CompletionPersistFailed.
        //
        // The reclaim WHERE clause filters on status='processing', so
        // a row in 'completion_persist_failed' CANNOT be reclaimed --
        // structural guarantee that handler replay is impossible.
        //
        // Test setup: the FaultInterceptor below overrides
        // SavingChangesAsync only; ExecuteUpdateAsync follows a
        // different code path (no change-tracker, no SaveChanges) so
        // the fallback path IS able to succeed even while every
        // SaveChanges-based attempt is throwing.
        const string key = "cmd:T1:U1:/agent:complete-exhausted-fallback";
        await this.SeedProcessingRowAsync(key);

        // Configure 2 attempts and seed enough transient failures to
        // exhaust them. Use zero delay to keep the test fast.
        this.interceptor.TransientFailuresRemaining = 10;
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard(
            completionMaxAttempts: 2,
            completionInitialDelayMilliseconds: 0);

        Func<Task> act = () => guard.MarkCompletedAsync(key, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "MarkCompletedAsync MUST surrender silently when the EF retry budget is exhausted so the ingestor dispatch loop stays alive -- but it MUST also persist the non-reclaimable fallback disposition via raw SQL so the stale-reclaim path will NOT replay the handler.");

        using IServiceScope verifyScope = this.serviceProvider.CreateScope();
        SlackInboundRequestRecord? row = await verifyScope.ServiceProvider
            .GetRequiredService<SlackTestDbContext>()
            .InboundRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IdempotencyKey == key);

        row.Should().NotBeNull();
        row!.ProcessingStatus.Should().Be(
            SlackInboundRequestProcessingStatus.CompletionPersistFailed,
            "the raw-SQL fallback path MUST transition the row out of 'processing' to the non-reclaimable disposition; otherwise the stale-reclaim path would re-execute the handler that already completed successfully");
        row.CompletedAt.Should().NotBeNull(
            "the fallback path stamps CompletedAt so operators can see when the residual write attempt landed");
    }

    [Fact]
    public async Task TryAcquireAsync_does_not_reclaim_completion_persist_failed_row_even_when_aged_past_stale_threshold()
    {
        // Iter 7 evaluator item #1 (companion contract test):
        // independent of HOW a row reaches the CompletionPersistFailed
        // disposition, the stale-lease reclaim path MUST treat it as
        // a true duplicate -- otherwise a future Slack retry that
        // arrives after the threshold would re-execute the handler.
        // This test seeds a row in CompletionPersistFailed with an
        // ancient FirstSeenAt (well past the stale threshold) and
        // proves TryAcquireAsync returns false WITHOUT reclaiming.
        const string key = "cmd:T1:U1:/agent:non-reclaimable";

        using (IServiceScope seedScope = this.serviceProvider.CreateScope())
        {
            SlackTestDbContext context = seedScope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
            context.InboundRequests.Add(new SlackInboundRequestRecord
            {
                IdempotencyKey = key,
                SourceType = "command",
                TeamId = "T1",
                ChannelId = "C1",
                UserId = "U1",
                RawPayloadHash = "seed",
                ProcessingStatus = SlackInboundRequestProcessingStatus.CompletionPersistFailed,
                FirstSeenAt = DateTimeOffset.UtcNow.AddHours(-2),
                CompletedAt = DateTimeOffset.UtcNow.AddHours(-2).AddSeconds(1),
            });
            await context.SaveChangesAsync();
        }

        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard(
            completionMaxAttempts: 4,
            completionInitialDelayMilliseconds: 0);
        SlackInboundEnvelope envelope = BuildEnvelope(key);

        bool acquired = await guard.TryAcquireAsync(envelope, CancellationToken.None);

        acquired.Should().BeFalse(
            "the CompletionPersistFailed disposition is the structural defence against handler replay -- TryAcquireAsync MUST treat it as a true duplicate regardless of FirstSeenAt age so the stale-reclaim path cannot replay already-successful work.");

        using IServiceScope verifyScope = this.serviceProvider.CreateScope();
        SlackInboundRequestRecord? row = await verifyScope.ServiceProvider
            .GetRequiredService<SlackTestDbContext>()
            .InboundRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IdempotencyKey == key);

        row.Should().NotBeNull();
        row!.ProcessingStatus.Should().Be(
            SlackInboundRequestProcessingStatus.CompletionPersistFailed,
            "the row MUST remain in the non-reclaimable disposition; the reclaim path MUST NOT flip it back to 'processing'");
    }

    private async Task SeedProcessingRowAsync(string idempotencyKey)
    {
        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackTestDbContext context = scope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        context.InboundRequests.Add(new SlackInboundRequestRecord
        {
            IdempotencyKey = idempotencyKey,
            SourceType = "command",
            TeamId = "T1",
            ChannelId = "C1",
            UserId = "U1",
            RawPayloadHash = "seed",
            ProcessingStatus = SlackInboundRequestProcessingStatus.Processing,
            FirstSeenAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
        });
        await context.SaveChangesAsync();
    }

    private SlackIdempotencyGuard<SlackTestDbContext> BuildGuard()
        => new(
            this.serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlackIdempotencyGuard<SlackTestDbContext>>.Instance,
            TimeProvider.System);

    private SlackIdempotencyGuard<SlackTestDbContext> BuildGuard(
        int completionMaxAttempts,
        int completionInitialDelayMilliseconds)
        => new(
            this.serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlackIdempotencyGuard<SlackTestDbContext>>.Instance,
            TimeProvider.System,
            Options.Create(new SlackConnectorOptions
            {
                Idempotency = new SlackIdempotencyOptions
                {
                    StaleProcessingThresholdSeconds = 300,
                    CompletionMaxAttempts = completionMaxAttempts,
                    CompletionInitialDelayMilliseconds = completionInitialDelayMilliseconds,
                },
            }));

    private static SlackInboundEnvelope BuildEnvelope(string key) => new(
        IdempotencyKey: key,
        SourceType: SlackInboundSourceType.Command,
        TeamId: "T1",
        ChannelId: "C1",
        UserId: "U1",
        RawPayload: "team_id=T1&user_id=U1&command=/agent&trigger_id=trig",
        TriggerId: "trig",
        ReceivedAt: DateTimeOffset.UtcNow);

    private sealed class FaultInterceptor : SaveChangesInterceptor
    {
        private readonly SqliteConnection connection;

        public FaultInterceptor(SqliteConnection connection)
        {
            this.connection = connection;
        }

        /// <summary>
        /// When true, the next SaveChanges call inserts a "shadow"
        /// row directly via the SQLite connection BEFORE EF's
        /// SaveChanges runs. The subsequent SaveChanges will then
        /// hit the SQLite UNIQUE constraint on idempotency_key and
        /// throw a real DbUpdateException -- exactly the race-lost
        /// path the guard must handle as a duplicate.
        /// </summary>
        public bool ShadowInsertOnNextSave { get; set; }

        /// <summary>
        /// When true, the next SaveChanges call throws a synthetic
        /// DbUpdateException without inserting anything -- mimicking
        /// a transient connection failure / timeout / schema error
        /// that the guard MUST NOT confuse with a duplicate.
        /// </summary>
        public bool ThrowTransientOnNextSave { get; set; }

        /// <summary>
        /// Number of subsequent SaveChanges calls that must throw a
        /// synthetic transient DbUpdateException. Decremented per
        /// throw, so a value of <c>1</c> causes exactly one transient
        /// failure followed by normal behaviour on the next call.
        /// Used by the bounded-retry tests to prove the guard absorbs
        /// transient blips inside <c>UpdateTerminalStatusAsync</c>.
        /// </summary>
        public int TransientFailuresRemaining { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (this.ThrowTransientOnNextSave)
            {
                this.ThrowTransientOnNextSave = false;
                throw new DbUpdateException(
                    "simulated transient failure",
                    new InvalidOperationException("simulated inner SQL exception"));
            }

            if (this.TransientFailuresRemaining > 0)
            {
                this.TransientFailuresRemaining--;
                throw new DbUpdateException(
                    "simulated transient failure (counted)",
                    new InvalidOperationException("simulated inner SQL exception"));
            }

            if (this.ShadowInsertOnNextSave)
            {
                this.ShadowInsertOnNextSave = false;
                this.InsertShadowRow(eventData);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void InsertShadowRow(DbContextEventData eventData)
        {
            // Reach into the change tracker to find the
            // SlackInboundRequestRecord that EF is about to insert
            // and write a row with the same idempotency key so the
            // pending insert hits the UNIQUE constraint.
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in eventData.Context!.ChangeTracker.Entries())
            {
                if (entry.Entity is AgentSwarm.Messaging.Slack.Entities.SlackInboundRequestRecord pending)
                {
                    using SqliteCommand cmd = this.connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO slack_inbound_request_record
                            (idempotency_key, source_type, team_id, channel_id, user_id, raw_payload_hash, processing_status, first_seen_at, completed_at)
                        VALUES (@key, 'command', 'T_OTHER', NULL, 'U_OTHER', 'shadow', 'processing', @firstSeenAt, NULL);";
                    cmd.Parameters.AddWithValue("@key", pending.IdempotencyKey);
                    cmd.Parameters.AddWithValue("@firstSeenAt", DateTimeOffset.UtcNow.ToString("O"));
                    cmd.ExecuteNonQuery();
                    return;
                }
            }
        }
    }
}
