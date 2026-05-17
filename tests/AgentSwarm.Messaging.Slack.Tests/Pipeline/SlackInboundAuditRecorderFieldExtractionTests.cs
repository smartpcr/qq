// -----------------------------------------------------------------------
// <copyright file="SlackInboundAuditRecorderFieldExtractionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.3 iter 6 regression coverage for the audit-field population
/// added to <see cref="SlackInboundAuditRecorder"/>. Verifies that
/// every persisted row carries CommandText, ThreadTs, MessageTs, and
/// ConversationId per the operator attachment's "Audit" requirement
/// ("Persist Slack team ID, channel ID, thread timestamp, user ID,
/// command text, and response payload").
/// </summary>
public sealed class SlackInboundAuditRecorderFieldExtractionTests
{
    [Fact]
    public async Task RecordSuccessAsync_populates_CommandText_from_slash_command_payload()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackInboundAuditRecorder recorder = new(
            writer,
            NullLogger<SlackInboundAuditRecorder>.Instance,
            TimeProvider.System);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "cmd:T1:U1:/agent:trig-1",
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: "team_id=T1&user_id=U1&command=%2Fagent&text=plan%20failover&trigger_id=trig-1",
            TriggerId: "trig-1",
            ReceivedAt: DateTimeOffset.UtcNow);

        await recorder.RecordSuccessAsync(envelope, requestType: null, CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.CommandText.Should().Be(
            "/agent plan failover",
            "the slash-command command + text MUST be persisted verbatim so an operator can grep the audit log for /agent invocations");
        entry.ThreadTs.Should().BeNull(
            "slash commands never originate inside a thread");
        entry.MessageTs.Should().BeNull(
            "slash commands have no message ts");
        entry.ConversationId.Should().Be(
            "C1",
            "when there is no thread, ConversationId MUST fall back to the channel id so per-conversation queries still group channel-scoped envelopes");
        entry.Outcome.Should().Be("success");
    }

    [Fact]
    public async Task RecordSuccessAsync_populates_ThreadTs_and_ConversationId_from_event_payload()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackInboundAuditRecorder recorder = new(
            writer,
            NullLogger<SlackInboundAuditRecorder>.Instance,
            TimeProvider.System);

        const string rawEvent = @"{
            ""token"": ""tok"",
            ""team_id"": ""T1"",
            ""event"": {
                ""type"": ""app_mention"",
                ""text"": ""<@U_BOT> review PR-42"",
                ""user"": ""U1"",
                ""ts"": ""1700000123.000200"",
                ""thread_ts"": ""1700000100.000100"",
                ""channel"": ""C1""
            },
            ""event_id"": ""Ev123""
        }";

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "event:Ev123",
            SourceType: SlackInboundSourceType.Event,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: rawEvent,
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        await recorder.RecordSuccessAsync(envelope, requestType: null, CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.CommandText.Should().Be(
            "<@U_BOT> review PR-42",
            "the event text MUST be persisted so an operator can query the audit log for the actual @mention content");
        entry.ThreadTs.Should().Be(
            "1700000100.000100",
            "the inner event.thread_ts MUST be lifted into the audit row so thread-scoped queries find this envelope");
        entry.MessageTs.Should().Be(
            "1700000123.000200",
            "the inner event.ts is the originating message timestamp; the operator needs it to anchor follow-up replies");
        entry.ConversationId.Should().Be(
            "1700000100.000100",
            "when the event lives inside a thread, ConversationId MUST be the thread_ts so all messages in the thread group together in audit queries");
    }

    [Fact]
    public async Task RecordSuccessAsync_populates_CommandText_from_interaction_action_id()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackInboundAuditRecorder recorder = new(
            writer,
            NullLogger<SlackInboundAuditRecorder>.Instance,
            TimeProvider.System);

        const string innerJson = @"{
            ""type"": ""block_actions"",
            ""team"": { ""id"": ""T1"" },
            ""user"": { ""id"": ""U1"" },
            ""trigger_id"": ""trig-x"",
            ""actions"": [
                { ""action_id"": ""approve_agent_plan_pr42"", ""value"": ""approve"" }
            ],
            ""container"": {
                ""type"": ""message"",
                ""thread_ts"": ""1700000050.000050"",
                ""message_ts"": ""1700000200.000300""
            }
        }";

        string raw = "payload=" + Uri.EscapeDataString(innerJson);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "interact:T1:U1:approve_agent_plan_pr42:trig-x",
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: raw,
            TriggerId: "trig-x",
            ReceivedAt: DateTimeOffset.UtcNow);

        await recorder.RecordSuccessAsync(envelope, requestType: null, CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.CommandText.Should().Be(
            "approve_agent_plan_pr42",
            "the action_id MUST be persisted as the command text so an operator can grep which button was clicked");
        entry.ThreadTs.Should().Be(
            "1700000050.000050",
            "the interaction's container.thread_ts MUST flow into the audit row so the thread-anchored conversation is queryable");
        entry.MessageTs.Should().Be(
            "1700000200.000300",
            "the interaction's container.message_ts MUST flow into the audit row so the originating message is queryable");
        entry.ConversationId.Should().Be(
            "1700000050.000050",
            "ConversationId MUST be the container.thread_ts to group every audit row for this thread together");
    }

    [Fact]
    public async Task RecordDuplicateAsync_still_populates_fields_so_duplicate_rows_are_queryable()
    {
        InMemorySlackAuditEntryWriter writer = new();
        SlackInboundAuditRecorder recorder = new(
            writer,
            NullLogger<SlackInboundAuditRecorder>.Instance,
            TimeProvider.System);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "cmd:T1:U1:/agent:trig-dup",
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T1",
            ChannelId: "C9",
            UserId: "U1",
            RawPayload: "team_id=T1&user_id=U1&command=%2Fagent&text=status",
            TriggerId: "trig-dup",
            ReceivedAt: DateTimeOffset.UtcNow);

        await recorder.RecordDuplicateAsync(envelope, requestType: null, CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be("duplicate");
        entry.CommandText.Should().Be(
            "/agent status",
            "even duplicate rows MUST carry the command text so an operator querying by command can see retry/duplicate noise alongside successful invocations");
        entry.ConversationId.Should().Be("C9");
    }

    [Fact]
    public async Task RecordSuccessAsync_populates_CommandText_from_view_submission_view_id_when_no_actions_present()
    {
        // Iter 7 evaluator item #2 (extended coverage): modal
        // submissions arrive as `view_submission` interactions that
        // do NOT carry an `actions` array; the extractor MUST fall
        // back to view.id so audit queries can still identify which
        // modal was submitted.
        InMemorySlackAuditEntryWriter writer = new();
        SlackInboundAuditRecorder recorder = new(
            writer,
            NullLogger<SlackInboundAuditRecorder>.Instance,
            TimeProvider.System);

        const string innerJson = @"{
            ""type"": ""view_submission"",
            ""team"": { ""id"": ""T1"" },
            ""user"": { ""id"": ""U1"" },
            ""trigger_id"": ""trig-modal"",
            ""view"": {
                ""id"": ""V_review_modal_42"",
                ""callback_id"": ""review_decision""
            }
        }";

        string raw = "payload=" + Uri.EscapeDataString(innerJson);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "interact:T1:U1:V_review_modal_42:trig-modal",
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: raw,
            TriggerId: "trig-modal",
            ReceivedAt: DateTimeOffset.UtcNow);

        await recorder.RecordSuccessAsync(envelope, requestType: null, CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.CommandText.Should().Be(
            "V_review_modal_42",
            "view_submission has no actions[] so the extractor MUST fall back to view.id; otherwise the audit log loses the modal identity for queries");
        entry.ThreadTs.Should().BeNull(
            "this modal submission has no container.thread_ts so ThreadTs MUST remain null");
        entry.MessageTs.Should().BeNull(
            "this modal submission has no container.message_ts so MessageTs MUST remain null");
        entry.ConversationId.Should().Be(
            "C1",
            "without a thread_ts the ConversationId MUST fall back to ChannelId per the documented mapping rule");
    }

    [Fact]
    public async Task RecordSuccessAsync_populates_MessageTs_only_for_event_without_thread_and_falls_back_to_channel_id()
    {
        // Iter-2 evaluator item 4 (Stage 5.2) STRUCTURAL update: a
        // top-level app_mention is replied to by SlackAppMentionHandler
        // by anchoring a NEW thread on event.ts. The audit row's
        // ThreadTs MUST therefore reflect that anchor (event.ts) so
        // a downstream operator can find this inbound row when
        // querying by the thread_ts the bot's reply created --
        // otherwise top-level @-mentions disappear from thread-scoped
        // audit queries (story FR-008 "Audit": "every agent/human
        // exchange is queryable by correlation ID" plus "Persist
        // ... thread timestamp"). Prior iter (Stage 4.3 iter 7) held
        // ThreadTs strictly null when event.thread_ts was missing;
        // Stage 5.2 supersedes that for app_mention specifically.
        InMemorySlackAuditEntryWriter writer = new();
        SlackInboundAuditRecorder recorder = new(
            writer,
            NullLogger<SlackInboundAuditRecorder>.Instance,
            TimeProvider.System);

        const string rawEvent = @"{
            ""token"": ""tok"",
            ""team_id"": ""T1"",
            ""event"": {
                ""type"": ""app_mention"",
                ""text"": ""<@U_BOT> status"",
                ""user"": ""U1"",
                ""ts"": ""1700000999.000700"",
                ""channel"": ""C42""
            },
            ""event_id"": ""Ev999""
        }";

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "event:Ev999",
            SourceType: SlackInboundSourceType.Event,
            TeamId: "T1",
            ChannelId: "C42",
            UserId: "U1",
            RawPayload: rawEvent,
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        await recorder.RecordSuccessAsync(envelope, requestType: null, CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.CommandText.Should().Be(
            "<@U_BOT> status",
            "the event text MUST flow into CommandText regardless of whether the message is threaded");
        entry.ThreadTs.Should().Be(
            "1700000999.000700",
            "Stage 5.2 SlackAppMentionHandler anchors a new thread on event.ts when the mention is top-level; ThreadTs MUST mirror that anchor so the audit row is discoverable from the thread the bot will reply into");
        entry.MessageTs.Should().Be(
            "1700000999.000700",
            "MessageTs MUST be populated even for non-threaded events so an outbound reply can anchor to the originating message");
        entry.ConversationId.Should().Be(
            "1700000999.000700",
            "Stage 5.2: ConversationId MUST be the new-thread anchor (event.ts) so every audit row tied to this conversation -- inbound mention + outbound reply -- groups under the same id");
    }

    [Fact]
    public async Task RecordErrorAsync_populates_audit_fields_and_error_detail_for_failed_envelopes()
    {
        // Iter 7 evaluator item #2 (extended coverage): the error /
        // DLQ row MUST also carry CommandText/ThreadTs/MessageTs so
        // an operator querying failed envelopes can still identify
        // which command/thread blew up.
        InMemorySlackAuditEntryWriter writer = new();
        SlackInboundAuditRecorder recorder = new(
            writer,
            NullLogger<SlackInboundAuditRecorder>.Instance,
            TimeProvider.System);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "cmd:T1:U1:/agent:trig-err",
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T1",
            ChannelId: "C77",
            UserId: "U1",
            RawPayload: "team_id=T1&user_id=U1&command=%2Fagent&text=review%20pr42",
            TriggerId: "trig-err",
            ReceivedAt: DateTimeOffset.UtcNow);

        await recorder.RecordErrorAsync(
            envelope,
            requestType: null,
            errorDetail: "handler exhausted retry budget after 3 attempts",
            CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(
            "error",
            "DLQ rows MUST be tagged with outcome=error so operators can filter on the failure cohort");
        entry.ErrorDetail.Should().Be(
            "handler exhausted retry budget after 3 attempts",
            "the terminal exception message MUST be persisted so triage does not require correlating to the DLQ row");
        entry.CommandText.Should().Be(
            "/agent review pr42",
            "error rows MUST still capture the command text so operators querying the failure cohort by command get matches");
        entry.ConversationId.Should().Be(
            "C77",
            "ConversationId MUST be populated on error rows so per-conversation failure queries work");
    }
}
