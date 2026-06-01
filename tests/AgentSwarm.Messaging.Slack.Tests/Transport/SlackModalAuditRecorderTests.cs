// -----------------------------------------------------------------------
// <copyright file="SlackModalAuditRecorderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.1 iter-3 evaluator item 3 unit tests for
/// <see cref="SlackModalAuditRecorder"/>. Pins the
/// <c>SlackAuditEntry</c> field shape (<c>direction=inbound</c>,
/// <c>request_type=modal_open</c>, <c>outcome=success|duplicate|error</c>)
/// that architecture.md §5.3 step 5 requires for every fast-path
/// invocation.
/// </summary>
public sealed class SlackModalAuditRecorderTests
{
    [Fact]
    public async Task RecordSuccessAsync_writes_inbound_modal_open_success_entry()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackModalAuditRecorder recorder = new(writer, NullLogger<SlackModalAuditRecorder>.Instance);

        await recorder.RecordSuccessAsync(BuildEnvelope(), "review", CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.Direction.Should().Be(SlackModalAuditRecorder.DirectionInbound);
        entry.RequestType.Should().Be(SlackModalAuditRecorder.RequestTypeModalOpen);
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeSuccess);
        entry.CommandText.Should().Be("/agent review");
        entry.ErrorDetail.Should().BeNull("success entries must not carry an error_detail");
        entry.TeamId.Should().Be("T01TEAM");
        entry.ChannelId.Should().Be("C01CHAN");
        entry.UserId.Should().Be("U01USER");
        entry.CorrelationId.Should().Be("cmd:test:1",
            "before the orchestrator assigns a correlation_id, the idempotency_key is the natural surrogate");
        entry.Id.Should().NotBeNullOrWhiteSpace("ULID must be generated");
    }

    [Fact]
    public async Task RecordDuplicateAsync_writes_inbound_modal_open_duplicate_entry_with_diagnostic_in_response_payload()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackModalAuditRecorder recorder = new(writer, NullLogger<SlackModalAuditRecorder>.Instance);

        await recorder.RecordDuplicateAsync(
            BuildEnvelope(),
            "escalate",
            "L2 row already exists",
            CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Single();
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeDuplicate);
        entry.RequestType.Should().Be(SlackModalAuditRecorder.RequestTypeModalOpen);
        entry.ResponsePayload.Should().Be("L2 row already exists",
            "duplicate diagnostics belong in response_payload so the audit row is queryable");
        entry.ErrorDetail.Should().BeNull("duplicate is not an error");
        entry.CommandText.Should().Be("/agent escalate");
    }

    [Fact]
    public async Task RecordErrorAsync_writes_inbound_modal_open_error_entry_with_detail()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackModalAuditRecorder recorder = new(writer, NullLogger<SlackModalAuditRecorder>.Instance);

        await recorder.RecordErrorAsync(
            BuildEnvelope(),
            "review",
            "views_open_NetworkFailure: timeout",
            CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Single();
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.ErrorDetail.Should().Be("views_open_NetworkFailure: timeout",
            "error rows MUST populate error_detail so the operator can grep by Slack error code");
        entry.ResponsePayload.Should().Be("views_open_NetworkFailure: timeout",
            "the detail also lands in response_payload for cross-column queries");
    }

    [Fact]
    public async Task Writer_exception_is_swallowed_so_the_user_facing_flow_is_not_disrupted()
    {
        ThrowingWriter writer = new();
        SlackModalAuditRecorder recorder = new(writer, NullLogger<SlackModalAuditRecorder>.Instance);

        // Should not throw -- best-effort write keeps the user-visible
        // ephemeral response stable even when the audit pipeline fails.
        await recorder.RecordSuccessAsync(BuildEnvelope(), "review", CancellationToken.None);

        writer.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task CorrelationId_falls_back_to_the_envelope_idempotency_key()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackModalAuditRecorder recorder = new(writer, NullLogger<SlackModalAuditRecorder>.Instance);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "cmd:T-X:U-X:/agent:trig-X",
            SourceType: SlackInboundSourceType.Command,
            TeamId: string.Empty,
            ChannelId: null,
            UserId: string.Empty,
            RawPayload: "ignored",
            TriggerId: "trig-X",
            ReceivedAt: DateTimeOffset.UtcNow);

        await recorder.RecordSuccessAsync(envelope, "review", CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Single();
        entry.CorrelationId.Should().Be("cmd:T-X:U-X:/agent:trig-X");
        entry.TeamId.Should().Be("unknown",
            "missing team_id is normalised to 'unknown' so the NOT NULL constraint never trips");
        entry.UserId.Should().BeNull("missing user_id stays null because the column is nullable");
    }

    private static SlackInboundEnvelope BuildEnvelope() => new(
        IdempotencyKey: "cmd:test:1",
        SourceType: SlackInboundSourceType.Command,
        TeamId: "T01TEAM",
        ChannelId: "C01CHAN",
        UserId: "U01USER",
        RawPayload: "team_id=T01TEAM&channel_id=C01CHAN&user_id=U01USER&command=/agent&trigger_id=trig",
        TriggerId: "trig",
        ReceivedAt: DateTimeOffset.UtcNow);

    private sealed class ThrowingWriter : ISlackAuditEntryWriter
    {
        public int Invocations { get; private set; }

        public Task AppendAsync(SlackAuditEntry entry, CancellationToken ct)
        {
            this.Invocations++;
            throw new InvalidOperationException("audit pipeline blew up");
        }
    }
}
