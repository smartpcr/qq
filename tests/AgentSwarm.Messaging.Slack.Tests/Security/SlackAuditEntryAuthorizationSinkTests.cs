// -----------------------------------------------------------------------
// <copyright file="SlackAuditEntryAuthorizationSinkTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Pins the field-by-field mapping between a
/// <see cref="SlackAuthorizationAuditRecord"/> and the canonical
/// <see cref="SlackAuditEntry"/> row written by
/// <see cref="SlackAuditEntryAuthorizationSink"/>. The brief mandates
/// the audit row include <c>team_id</c>, <c>channel_id</c>, and
/// <c>user_id</c>; the tests below assert each of those fields makes it
/// through the bridge.
/// </summary>
public sealed class SlackAuditEntryAuthorizationSinkTests
{
    [Fact]
    public void Map_copies_team_channel_user_and_sets_rejected_auth_outcome()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        SlackAuthorizationAuditRecord record = new(
            ReceivedAt: now,
            Reason: SlackAuthorizationRejectionReason.DisallowedChannel,
            Outcome: SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            RequestPath: "/api/slack/commands",
            TeamId: "T0123ABCD",
            ChannelId: "C9999ALPHA",
            UserId: "U7777BETA",
            CommandText: "/agent ask hello",
            ErrorDetail: "channel 'C9999ALPHA' not in AllowedChannelIds for team 'T0123ABCD'.");

        SlackAuditEntry entry = SlackAuditEntryAuthorizationSink.Map(record);

        entry.TeamId.Should().Be("T0123ABCD");
        entry.ChannelId.Should().Be("C9999ALPHA");
        entry.UserId.Should().Be("U7777BETA");
        entry.Outcome.Should().Be(SlackAuthorizationAuditRecord.RejectedAuthOutcome);
        entry.Direction.Should().Be("inbound");
        entry.RequestType.Should().Be("slash_command");
        entry.CommandText.Should().Be("/agent ask hello",
            "the raw slash-command text is preserved so audit-replay can reconstruct the request");
        entry.Timestamp.Should().Be(now);
        entry.ErrorDetail.Should().Contain("DisallowedChannel");
        entry.Id.Should().HaveLength(26, "audit ids are ULID-shaped per architecture.md §3.5");
    }

    [Fact]
    public void Map_uses_unknown_placeholder_when_team_id_is_missing()
    {
        SlackAuthorizationAuditRecord record = new(
            ReceivedAt: DateTimeOffset.UtcNow,
            Reason: SlackAuthorizationRejectionReason.MissingTeamId,
            Outcome: SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            RequestPath: "/api/slack/events",
            TeamId: null,
            ChannelId: null,
            UserId: null,
            CommandText: null,
            ErrorDetail: "team_id is missing");

        SlackAuditEntry entry = SlackAuditEntryAuthorizationSink.Map(record);

        entry.TeamId.Should().Be(SlackAuditEntryAuthorizationSink.UnknownTeamIdPlaceholder,
            "the slack_audit_entry.team_id column is non-nullable; a stable placeholder keeps triage queries deterministic");
        entry.RequestType.Should().Be("event");
        entry.CommandText.Should().Contain("MissingTeamId");
    }

    [Theory]
    [InlineData("/api/slack/events", "event")]
    [InlineData("/api/slack/commands", "slash_command")]
    [InlineData("/api/slack/interactions", "interaction")]
    [InlineData("/api/slack/other", "authorization_rejection")]
    public void Map_derives_request_type_from_path(string path, string expectedRequestType)
    {
        SlackAuthorizationAuditRecord record = new(
            ReceivedAt: DateTimeOffset.UtcNow,
            Reason: SlackAuthorizationRejectionReason.UnknownWorkspace,
            Outcome: SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            RequestPath: path,
            TeamId: "T0",
            ChannelId: null,
            UserId: null,
            CommandText: null,
            ErrorDetail: null);

        SlackAuditEntry entry = SlackAuditEntryAuthorizationSink.Map(record);

        entry.RequestType.Should().Be(expectedRequestType);
    }

    [Fact]
    public async Task WriteAsync_forwards_to_writer()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackAuditEntryAuthorizationSink sink = new(writer, NullLogger<SlackAuditEntryAuthorizationSink>.Instance);

        SlackAuthorizationAuditRecord record = new(
            ReceivedAt: DateTimeOffset.UtcNow,
            Reason: SlackAuthorizationRejectionReason.UserNotInAllowedGroup,
            Outcome: SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            RequestPath: "/api/slack/commands",
            TeamId: "T0",
            ChannelId: "C0",
            UserId: "U0",
            CommandText: null,
            ErrorDetail: null);

        await sink.WriteAsync(record, CancellationToken.None);

        writer.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(SlackAuthorizationAuditRecord.RejectedAuthOutcome);
    }

    [Fact]
    public async Task WriteAsync_swallows_writer_failure()
    {
        ThrowingWriter writer = new();
        SlackAuditEntryAuthorizationSink sink = new(writer, NullLogger<SlackAuditEntryAuthorizationSink>.Instance);

        SlackAuthorizationAuditRecord record = new(
            ReceivedAt: DateTimeOffset.UtcNow,
            Reason: SlackAuthorizationRejectionReason.UnknownWorkspace,
            Outcome: SlackAuthorizationAuditRecord.RejectedAuthOutcome,
            RequestPath: "/api/slack/commands",
            TeamId: "T0",
            ChannelId: null,
            UserId: null,
            CommandText: null,
            ErrorDetail: null);

        Func<Task> act = () => sink.WriteAsync(record, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "the filter has already decided to reject; an audit-write failure must not escalate to the response pipeline");
    }

    private sealed class ThrowingWriter : ISlackAuditEntryWriter
    {
        public Task AppendAsync(SlackAuditEntry entry, CancellationToken ct)
            => Task.FromException(new InvalidOperationException("audit table missing"));
    }
}
