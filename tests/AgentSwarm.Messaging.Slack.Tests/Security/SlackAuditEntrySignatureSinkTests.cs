// -----------------------------------------------------------------------
// <copyright file="SlackAuditEntrySignatureSinkTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Identifiers;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 3.1 evaluator-feedback regression tests for
/// <see cref="SlackAuditEntrySignatureSink"/>. The iter-1 review flagged
/// that "no changed file maps SlackSignatureAuditRecord into
/// SlackAuditEntry" -- these tests pin the mapping AND prove the sink
/// actually appends the entry through <see cref="ISlackAuditEntryWriter"/>.
/// </summary>
public sealed class SlackAuditEntrySignatureSinkTests
{
    [Fact]
    public void Map_populates_audit_entry_with_canonical_outcome_and_inbound_direction()
    {
        SlackSignatureAuditRecord record = new(
            ReceivedAt: DateTimeOffset.FromUnixTimeSeconds(1_714_410_000),
            Reason: SlackSignatureRejectionReason.SignatureMismatch,
            Outcome: SlackSignatureAuditRecord.RejectedSignatureOutcome,
            RequestPath: "/api/slack/events",
            TeamId: "T0123ABCD",
            SignatureHeader: "v0=" + new string('a', 64),
            TimestampHeader: "1714410000",
            ErrorDetail: "HMAC mismatch");

        SlackAuditEntry entry = SlackAuditEntrySignatureSink.Map(record);

        entry.Should().NotBeNull();
        entry.Outcome.Should().Be(SlackAuditEntry_OutcomeRejectedSignature(),
            "the brief mandates outcome = rejected_signature for signature rejections");
        entry.Direction.Should().Be("inbound");
        entry.TeamId.Should().Be("T0123ABCD");
        entry.RequestType.Should().Be("event",
            "/api/slack/events maps to request_type=event so triage queries can filter by audit shape");
        entry.Timestamp.Should().Be(record.ReceivedAt);
        entry.Id.Should().NotBeNullOrWhiteSpace();
        entry.Id.Length.Should().Be(Ulid.Length,
            "architecture.md §3.5 (and SlackAuditEntry.Id's XML doc) require the id to be a ULID-shaped 26-char Crockford base32 string");
        Ulid.IsValid(entry.Id).Should().BeTrue(
            "the audit id must be a well-formed ULID so triage tools that parse the timestamp prefix do not break");
        entry.CorrelationId.Should().NotBeNullOrWhiteSpace();
        Ulid.IsValid(entry.CorrelationId).Should().BeTrue(
            "the correlation id reuses the audit id and therefore must satisfy the same ULID shape contract");
        entry.ErrorDetail.Should().Contain("SignatureMismatch");
        entry.ErrorDetail.Should().Contain("HMAC mismatch");
        entry.CommandText.Should().Contain("/api/slack/events");
        entry.CommandText.Should().Contain("SignatureMismatch");
        entry.ResponsePayload.Should().BeNull();
        entry.AgentId.Should().BeNull();
        entry.TaskId.Should().BeNull();
    }

    [Theory]
    [InlineData("/api/slack/events", "event")]
    [InlineData("/api/slack/commands", "slash_command")]
    [InlineData("/api/slack/interactions", "interaction")]
    [InlineData("/api/slack/something-else", "signature_rejection")]
    [InlineData("", "signature_rejection")]
    public void Map_derives_request_type_from_request_path(string path, string expected)
    {
        SlackSignatureAuditRecord record = NewRecord(requestPath: path);
        SlackAuditEntry entry = SlackAuditEntrySignatureSink.Map(record);
        entry.RequestType.Should().Be(expected);
    }

    [Fact]
    public void Map_substitutes_placeholder_team_id_when_record_has_no_team_id()
    {
        SlackSignatureAuditRecord record = NewRecord(teamId: null);
        SlackAuditEntry entry = SlackAuditEntrySignatureSink.Map(record);
        entry.TeamId.Should().Be(SlackAuditEntrySignatureSink.UnknownTeamIdPlaceholder,
            "the slack_audit_entry.team_id column is non-nullable; a stable placeholder keeps triage queries deterministic");
    }

    [Fact]
    public async Task WriteAsync_appends_an_entry_through_the_configured_writer()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackAuditEntrySignatureSink sink = new(writer, NullLogger<SlackAuditEntrySignatureSink>.Instance);

        SlackSignatureAuditRecord record = NewRecord();
        await sink.WriteAsync(record, CancellationToken.None);

        writer.Entries.Should().ContainSingle(
            "every signature rejection must land as exactly one slack_audit_entry row");
        writer.Entries[0].Outcome.Should().Be(SlackAuditEntry_OutcomeRejectedSignature());
    }

    [Fact]
    public async Task WriteAsync_swallows_writer_failures_so_the_HTTP_response_is_not_affected()
    {
        ThrowingWriter writer = new();
        SlackAuditEntrySignatureSink sink = new(writer, NullLogger<SlackAuditEntrySignatureSink>.Instance);

        SlackSignatureAuditRecord record = NewRecord();
        Func<Task> act = async () => await sink.WriteAsync(record, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "audit-write failures must be logged but never break the request response (the rejection has already been decided)");
        writer.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task WriteAsync_propagates_cancellation_when_caller_cancels()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackAuditEntrySignatureSink sink = new(writer, NullLogger<SlackAuditEntrySignatureSink>.Instance);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await sink.WriteAsync(NewRecord(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "an explicit caller cancellation must surface so the request abort path is honoured");
    }

    private static SlackSignatureAuditRecord NewRecord(
        string requestPath = "/api/slack/events",
        string? teamId = "T0123ABCD")
    {
        return new SlackSignatureAuditRecord(
            ReceivedAt: DateTimeOffset.FromUnixTimeSeconds(1_714_410_000),
            Reason: SlackSignatureRejectionReason.SignatureMismatch,
            Outcome: SlackSignatureAuditRecord.RejectedSignatureOutcome,
            RequestPath: requestPath,
            TeamId: teamId,
            SignatureHeader: "v0=deadbeef",
            TimestampHeader: "1714410000",
            ErrorDetail: "test");
    }

    private static string SlackAuditEntry_OutcomeRejectedSignature()
        => SlackSignatureAuditRecord.RejectedSignatureOutcome;

    private sealed class ThrowingWriter : ISlackAuditEntryWriter
    {
        public int CallCount { get; private set; }

        public Task AppendAsync(SlackAuditEntry entry, CancellationToken ct)
        {
            this.CallCount++;
            throw new InvalidOperationException("simulated DB outage");
        }
    }
}
