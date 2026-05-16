// -----------------------------------------------------------------------
// <copyright file="InMemorySlackIdempotencyGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

/// <summary>
/// Contract-parity tests for <see cref="InMemorySlackIdempotencyGuard"/>.
/// </summary>
public sealed class InMemorySlackIdempotencyGuardTests
{
    [Fact]
    public async Task TryAcquireAsync_returns_true_for_new_key_and_defers_recent_processing_on_second_call()
    {
        // The first call inserts a 'processing' row. The second call
        // is back-to-back, so the row is well within the stale-lease
        // window -- the guard sees a LIVE LEASE and DEFERS the
        // redelivery (returns false), it does NOT report a "true
        // duplicate" because the handler has not run yet.
        InMemorySlackIdempotencyGuard guard = new();
        SlackInboundEnvelope envelope = BuildEnvelope("event:Ev1");

        (await guard.TryAcquireAsync(envelope, CancellationToken.None)).Should().BeTrue();
        (await guard.TryAcquireAsync(envelope, CancellationToken.None)).Should().BeFalse(
            "recent in-flight lease defers the redelivery per architecture.md §2.6 (live lease, not a true duplicate)");

        guard.Snapshot.Should().ContainKey("event:Ev1");
        guard.Snapshot["event:Ev1"].ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.Processing);
    }

    [Fact]
    public async Task MarkCompletedAsync_flips_status_to_completed_and_stamps_completed_at()
    {
        InMemorySlackIdempotencyGuard guard = new();
        SlackInboundEnvelope envelope = BuildEnvelope("event:Ev2");

        await guard.TryAcquireAsync(envelope, CancellationToken.None);
        await guard.MarkCompletedAsync(envelope.IdempotencyKey, CancellationToken.None);

        guard.Snapshot["event:Ev2"].ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.Completed);
        guard.Snapshot["event:Ev2"].CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_flips_status_to_failed_and_stamps_completed_at()
    {
        InMemorySlackIdempotencyGuard guard = new();
        SlackInboundEnvelope envelope = BuildEnvelope("event:Ev3");

        await guard.TryAcquireAsync(envelope, CancellationToken.None);
        await guard.MarkFailedAsync(envelope.IdempotencyKey, CancellationToken.None);

        guard.Snapshot["event:Ev3"].ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.Failed);
        guard.Snapshot["event:Ev3"].CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkCompletedAsync_on_missing_row_does_not_throw_and_does_not_create_a_new_row()
    {
        InMemorySlackIdempotencyGuard guard = new();

        Func<Task> act = () => guard.MarkCompletedAsync("event:nope", CancellationToken.None);

        await act.Should().NotThrowAsync();
        guard.Snapshot.Should().NotContainKey("event:nope",
            "missing row is silently dropped, NOT auto-created as a side-effect of MarkCompleted");
    }

    [Fact]
    public async Task MarkCompletedAsync_does_not_clobber_modal_opened_rows()
    {
        InMemorySlackIdempotencyGuard guard = new();
        guard.Preload("cmd:T:U:/agent:trig", SlackInboundRequestProcessingStatus.ModalOpened);

        await guard.MarkCompletedAsync("cmd:T:U:/agent:trig", CancellationToken.None);

        guard.Snapshot["cmd:T:U:/agent:trig"].ProcessingStatus.Should().Be(
            SlackInboundRequestProcessingStatus.ModalOpened,
            "fast-path terminal status must survive the async ingestor's MarkCompleted call");
    }

    [Fact]
    public async Task TryAcquireAsync_rejects_envelope_with_empty_idempotency_key()
    {
        InMemorySlackIdempotencyGuard guard = new();
        SlackInboundEnvelope envelope = BuildEnvelope(string.Empty);

        Func<Task> act = () => guard.TryAcquireAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryAcquireAsync_defers_recent_processing_row_as_duplicate()
    {
        // architecture.md §2.6: an in-progress event is DEFERRED so a
        // healthy mid-retry worker is not preempted by a Slack
        // redelivery. The recent-processing row must therefore NOT be
        // reclaimed.
        InMemorySlackIdempotencyGuard guard = new(
            timeProvider: TimeProvider.System,
            options: Microsoft.Extensions.Options.Options.Create(new AgentSwarm.Messaging.Slack.Configuration.SlackConnectorOptions
            {
                Idempotency = { StaleProcessingThresholdSeconds = 300 },
            }));

        // Preload a row 10 seconds old -- well under the 5-minute
        // stale threshold.
        guard.Preload(
            "event:Ev-recent",
            SlackInboundRequestProcessingStatus.Processing,
            DateTimeOffset.UtcNow.AddSeconds(-10));

        bool acquired = await guard.TryAcquireAsync(BuildEnvelope("event:Ev-recent"), CancellationToken.None);

        acquired.Should().BeFalse("recent processing row defers the redelivery");
        guard.Snapshot["event:Ev-recent"].ProcessingStatus.Should().Be(
            SlackInboundRequestProcessingStatus.Processing,
            "the deferred redelivery must NOT rewrite the existing row");
    }

    [Fact]
    public async Task TryAcquireAsync_reclaims_stale_processing_row_so_crashed_worker_recovers()
    {
        // architecture.md §2.6 + this iter's fix: an in-progress row
        // older than the stale-lease threshold MUST be reclaimable, or
        // a worker that crashed mid-flow leaves the row stuck forever
        // and every future Slack retry is silently duplicate-audited.
        InMemorySlackIdempotencyGuard guard = new(
            timeProvider: TimeProvider.System,
            options: Microsoft.Extensions.Options.Options.Create(new AgentSwarm.Messaging.Slack.Configuration.SlackConnectorOptions
            {
                Idempotency = { StaleProcessingThresholdSeconds = 1 },
            }));

        // Preload a row that is artificially aged 2 hours -- well
        // past any sane stale threshold so the test is deterministic
        // against clock jitter.
        DateTimeOffset stalePreviousFirstSeenAt = DateTimeOffset.UtcNow.AddHours(-2);
        guard.Preload(
            "event:Ev-stale",
            SlackInboundRequestProcessingStatus.Processing,
            stalePreviousFirstSeenAt);

        bool acquired = await guard.TryAcquireAsync(BuildEnvelope("event:Ev-stale"), CancellationToken.None);

        acquired.Should().BeTrue("stale processing row must be reclaimable for crash recovery");
        InMemorySlackIdempotencyGuard.Entry row = guard.Snapshot["event:Ev-stale"];
        row.ProcessingStatus.Should().Be(SlackInboundRequestProcessingStatus.Processing);
        row.FirstSeenAt.Should().BeAfter(stalePreviousFirstSeenAt,
            "reclaim MUST bump FirstSeenAt to now so concurrent reclaims see a fresh lease");
        row.CompletedAt.Should().BeNull("reclaimed lease starts a new attempt");
    }

    private static SlackInboundEnvelope BuildEnvelope(string key) => new(
        IdempotencyKey: key,
        SourceType: SlackInboundSourceType.Event,
        TeamId: "T1",
        ChannelId: "C1",
        UserId: "U1",
        RawPayload: "{}",
        TriggerId: null,
        ReceivedAt: DateTimeOffset.UtcNow);
}
