// -----------------------------------------------------------------------
// <copyright file="SlackIdempotencyGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Tests.Persistence;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.3 tests for <see cref="SlackIdempotencyGuard{TContext}"/>.
/// Drives the guard against an in-memory SQLite-backed
/// <see cref="SlackTestDbContext"/> so the production
/// <c>slack_inbound_request_record</c> schema is exercised verbatim.
/// </summary>
public sealed class SlackIdempotencyGuardTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public SlackIdempotencyGuardTests()
    {
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        ServiceCollection services = new();
        services.AddDbContext<SlackTestDbContext>(opts => opts.UseSqlite(this.connection));
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
    public async Task TryAcquireAsync_inserts_processing_row_on_first_call()
    {
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();
        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig-fresh");

        bool acquired = await guard.TryAcquireAsync(envelope, CancellationToken.None);

        acquired.Should().BeTrue("the key has never been seen");

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        SlackInboundRequestRecord row = ctx.InboundRequests.Single();
        row.IdempotencyKey.Should().Be("cmd:T1:U1:/agent:trig-fresh");
        row.ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.Processing);
        row.SourceType.Should().Be("command");
        row.TeamId.Should().Be("T1");
        row.ChannelId.Should().Be("C1");
        row.UserId.Should().Be("U1");
        row.RawPayloadHash.Should().NotBeNullOrWhiteSpace();
        row.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_returns_false_on_second_call_because_recent_processing_lease_defers()
    {
        // The first call inserts a row in 'processing'. The second
        // call -- back-to-back, so well under the 5-minute stale
        // window -- observes a RECENT 'processing' lease still owned
        // by the in-flight worker and DEFERS per architecture.md §2.6.
        // The contract returns false so an in-flight worker is not
        // preempted. NOTE: this is the "deferred live lease" branch,
        // NOT the "true duplicate" branch (which fires for terminal /
        // fast-path rows). Both branches share the 'outcome =
        // duplicate' audit marker but mean different things to an
        // operator; see the theory test below for the duplicate path.
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();
        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig-dup");

        (await guard.TryAcquireAsync(envelope, CancellationToken.None)).Should().BeTrue();
        (await guard.TryAcquireAsync(envelope, CancellationToken.None)).Should().BeFalse(
            "a recent in-flight 'processing' lease defers the redelivery per architecture.md §2.6 (live lease, NOT true duplicate)");

        using IServiceScope scope = this.serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("reserved")]
    [InlineData("modal_opened")]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("received")]
    public async Task TryAcquireAsync_treats_terminal_and_fast_path_statuses_as_true_duplicate(string preexistingStatus)
    {
        // Covers ONLY the terminal statuses (completed / failed) and
        // the Stage 4.1 fast-path statuses (reserved / modal_opened /
        // received) -- i.e. the rows for which the handler has
        // already run (or the fast-path already owns the request) and
        // the redelivery is therefore a TRUE DUPLICATE. 'processing'
        // is intentionally OMITTED here because that status has TWO
        // distinct lease outcomes that are NOT true duplicates:
        // recent rows defer (covered by
        // TryAcquireAsync_defers_recent_processing_row_as_duplicate)
        // and stale rows reclaim (covered by
        // TryAcquireAsync_reclaims_stale_processing_row_so_crashed_worker_recovers).
        // For the statuses below, every redelivery MUST return false
        // so the handler does not run a second time -- mirroring
        // both the Stage 4.1 fast-path coexistence rule and the
        // cross-replica retry scenarios.
        string key = $"cmd:T1:U1:/agent:trig-{preexistingStatus}";
        using (IServiceScope seed = this.serviceProvider.CreateScope())
        {
            SlackTestDbContext ctx = seed.ServiceProvider.GetRequiredService<SlackTestDbContext>();
            ctx.InboundRequests.Add(new SlackInboundRequestRecord
            {
                IdempotencyKey = key,
                SourceType = "command",
                TeamId = "T1",
                ChannelId = "C1",
                UserId = "U1",
                RawPayloadHash = "preexisting",
                ProcessingStatus = preexistingStatus,
                FirstSeenAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = preexistingStatus is "completed" or "failed" or "modal_opened"
                    ? DateTimeOffset.UtcNow
                    : null,
            });
            await ctx.SaveChangesAsync();
        }

        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();
        SlackInboundEnvelope envelope = BuildEnvelope(key);

        bool acquired = await guard.TryAcquireAsync(envelope, CancellationToken.None);

        acquired.Should().BeFalse(
            $"existing '{preexistingStatus}' row represents a finished (terminal) or already-claimed (fast-path) request -- the redelivery is a TRUE duplicate and the handler must NOT run again");
    }

    [Fact]
    public async Task MarkCompletedAsync_flips_status_to_completed_and_stamps_completed_at()
    {
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();
        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig-ok");

        await guard.TryAcquireAsync(envelope, CancellationToken.None);
        await guard.MarkCompletedAsync(envelope.IdempotencyKey, CancellationToken.None);

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackInboundRequestRecord row = scope.ServiceProvider
            .GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Single();
        row.ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.Completed);
        row.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_flips_status_to_failed_and_stamps_completed_at()
    {
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();
        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig-bad");

        await guard.TryAcquireAsync(envelope, CancellationToken.None);
        await guard.MarkFailedAsync(envelope.IdempotencyKey, CancellationToken.None);

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackInboundRequestRecord row = scope.ServiceProvider
            .GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Single();
        row.ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.Failed);
        row.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkCompletedAsync_does_not_clobber_modal_opened_rows()
    {
        // Seed a modal_opened row written by the Stage 4.1 fast-path,
        // then ensure the async ingestor's MarkCompletedAsync leaves
        // it alone.
        string key = "cmd:T1:U1:/agent:trig-modal";
        using (IServiceScope seed = this.serviceProvider.CreateScope())
        {
            SlackTestDbContext ctx = seed.ServiceProvider.GetRequiredService<SlackTestDbContext>();
            ctx.InboundRequests.Add(new SlackInboundRequestRecord
            {
                IdempotencyKey = key,
                SourceType = "command",
                TeamId = "T1",
                ChannelId = "C1",
                UserId = "U1",
                RawPayloadHash = "x",
                ProcessingStatus = SlackInboundRequestProcessingStatus.ModalOpened,
                FirstSeenAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await ctx.SaveChangesAsync();
        }

        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();
        await guard.MarkCompletedAsync(key, CancellationToken.None);

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackInboundRequestRecord row = scope.ServiceProvider
            .GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Single();
        row.ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.ModalOpened,
            "fast-path terminal status MUST survive the async ingestor's MarkCompleted call");
    }

    [Fact]
    public async Task MarkCompletedAsync_on_missing_row_does_not_throw()
    {
        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuard();

        Func<Task> act = () => guard.MarkCompletedAsync("cmd:none", CancellationToken.None);

        await act.Should().NotThrowAsync(
            "best-effort contract: a missing row must NOT crash the ingestor dispatch loop");
    }

    [Fact]
    public void SlackInboundRequestProcessingStatus_constants_stay_in_lockstep_with_fast_path_store()
    {
        // Pin the literal values so a future rename of either side
        // (Pipeline.SlackInboundRequestProcessingStatus or
        // Transport.EntityFrameworkSlackFastPathIdempotencyStore<T>.ProcessingStatus*)
        // is caught here at build time -- both must agree because
        // they index the same processing_status column.
        SlackInboundRequestProcessingStatus.Reserved.Should().Be(
            EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext>.ProcessingStatusReserved);
        SlackInboundRequestProcessingStatus.ModalOpened.Should().Be(
            EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext>.ProcessingStatusModalOpened);
    }

    [Fact]
    public async Task TryAcquireAsync_defers_recent_processing_row_as_duplicate()
    {
        // architecture.md §2.6: in-progress events DEFER -- a healthy
        // mid-retry worker must not be preempted by a Slack
        // redelivery, so a recent 'processing' row does NOT acquire.
        string key = "cmd:T1:U1:/agent:trig-recent-proc";
        DateTimeOffset recentFirstSeen = DateTimeOffset.UtcNow.AddSeconds(-15);
        using (IServiceScope seed = this.serviceProvider.CreateScope())
        {
            SlackTestDbContext ctx = seed.ServiceProvider.GetRequiredService<SlackTestDbContext>();
            ctx.InboundRequests.Add(new SlackInboundRequestRecord
            {
                IdempotencyKey = key,
                SourceType = "command",
                TeamId = "T1",
                ChannelId = "C1",
                UserId = "U1",
                RawPayloadHash = "preexisting",
                ProcessingStatus = SlackInboundRequestProcessingStatus.Processing,
                FirstSeenAt = recentFirstSeen,
                CompletedAt = null,
            });
            await ctx.SaveChangesAsync();
        }

        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuardWithStaleThreshold(300);

        bool acquired = await guard.TryAcquireAsync(BuildEnvelope(key), CancellationToken.None);

        acquired.Should().BeFalse("recent processing row defers to the live in-flight lease (NOT a true duplicate -- the original handler is still running)");

        using IServiceScope verify = this.serviceProvider.CreateScope();
        SlackInboundRequestRecord row = verify.ServiceProvider
            .GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Single();
        row.FirstSeenAt.Should().BeCloseTo(recentFirstSeen, TimeSpan.FromSeconds(1),
            "the deferred redelivery must NOT bump the existing FirstSeenAt");
    }

    [Fact]
    public async Task TryAcquireAsync_reclaims_stale_processing_row_so_crashed_worker_recovers()
    {
        // architecture.md §2.6 + this iter's fix: an in-progress row
        // older than the stale-lease threshold MUST be reclaimable so
        // a worker that crashed mid-flow does not leave the row stuck
        // and every Slack retry silently duplicate-audited.
        string key = "cmd:T1:U1:/agent:trig-stale-proc";
        DateTimeOffset staleFirstSeen = DateTimeOffset.UtcNow.AddHours(-2);
        using (IServiceScope seed = this.serviceProvider.CreateScope())
        {
            SlackTestDbContext ctx = seed.ServiceProvider.GetRequiredService<SlackTestDbContext>();
            ctx.InboundRequests.Add(new SlackInboundRequestRecord
            {
                IdempotencyKey = key,
                SourceType = "command",
                TeamId = "T1",
                ChannelId = "C1",
                UserId = "U1",
                RawPayloadHash = "preexisting",
                ProcessingStatus = SlackInboundRequestProcessingStatus.Processing,
                FirstSeenAt = staleFirstSeen,
                CompletedAt = null,
            });
            await ctx.SaveChangesAsync();
        }

        SlackIdempotencyGuard<SlackTestDbContext> guard = this.BuildGuardWithStaleThreshold(1);

        bool acquired = await guard.TryAcquireAsync(BuildEnvelope(key), CancellationToken.None);

        acquired.Should().BeTrue("stale processing row must be reclaimable for crash recovery");

        using IServiceScope verify = this.serviceProvider.CreateScope();
        SlackInboundRequestRecord row = verify.ServiceProvider
            .GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Single();
        row.ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.Processing);
        row.FirstSeenAt.Should().BeAfter(staleFirstSeen,
            "the OCC reclaim MUST bump FirstSeenAt to now so concurrent reclaimers observe a fresh lease");
        row.CompletedAt.Should().BeNull("reclaimed lease starts a new attempt");
    }

    private SlackIdempotencyGuard<SlackTestDbContext> BuildGuard()
        => new(
            this.serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlackIdempotencyGuard<SlackTestDbContext>>.Instance,
            TimeProvider.System);

    private SlackIdempotencyGuard<SlackTestDbContext> BuildGuardWithStaleThreshold(int seconds)
        => new(
            this.serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlackIdempotencyGuard<SlackTestDbContext>>.Instance,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new AgentSwarm.Messaging.Slack.Configuration.SlackConnectorOptions
            {
                Idempotency = { StaleProcessingThresholdSeconds = seconds },
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
}
