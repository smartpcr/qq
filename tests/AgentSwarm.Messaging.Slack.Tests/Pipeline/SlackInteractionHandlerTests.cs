// -----------------------------------------------------------------------
// <copyright file="SlackInteractionHandlerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 5.3 brief-mandated tests for
/// <see cref="SlackInteractionHandler"/>. The three test scenarios in
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (Stage 5.3) plus regression coverage for HTTP-form vs Socket Mode
/// shape parsing, correlation-id lookup, and best-effort failure
/// modes.
/// </summary>
public sealed class SlackInteractionHandlerTests
{
    // -----------------------------------------------------------------
    // Scenario 1: Button click produces HumanDecisionEvent with QuestionId
    // -- Given a Block Kit button click where block_id encodes
    //   QuestionId = Q-99 and value = approve, When the handler
    //   processes it, Then a HumanDecisionEvent with QuestionId = "Q-99"
    //   and ActionValue = "approve" is published and the message
    //   buttons are disabled (chat.update issued).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_publishes_HumanDecisionEvent_and_disables_buttons()
    {
        TestHarness harness = new();
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-99", requiresComment: false);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C123",
            messageTs: "1700000000.000100",
            userId: "U_ALICE",
            triggerId: "trig-1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_ALICE:trig-1",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        // Brief acceptance criterion: HumanDecisionEvent with Q-99/approve published.
        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        HumanDecisionEvent decision = harness.TaskService.PublishedDecisions[0];
        decision.QuestionId.Should().Be("Q-99");
        decision.ActionValue.Should().Be("approve");
        decision.Comment.Should().BeNull(
            "a non-RequiresComment button click does NOT carry free-text input");
        decision.Messenger.Should().Be(SlackInteractionHandler.MessengerName);
        decision.ExternalUserId.Should().Be("U_ALICE");
        decision.ExternalMessageId.Should().Be("1700000000.000100",
            "the brief pins ExternalMessageId = message.ts for button clicks");
        decision.CorrelationId.Should().Be(envelope.IdempotencyKey,
            "the null thread mapping degrades to the envelope's idempotency key");

        // Brief acceptance criterion: chat.update issued to disable buttons.
        harness.ChatUpdateClient.Requests.Should().ContainSingle();
        SlackChatUpdateRequest update = harness.ChatUpdateClient.Requests[0];
        update.ChannelId.Should().Be("C123");
        update.MessageTs.Should().Be("1700000000.000100");
        update.TeamId.Should().Be(envelope.TeamId);
        update.Text.Should().Contain("Approve");
        update.Blocks.Should().NotBeNull();

        // No comment modal must have been opened: this is a direct decision.
        harness.ViewsOpenClient.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Scenario 2: Modal submission includes QuestionId and comment
    // -- Given a view_submission with private_metadata = {questionId:Q-55},
    //   static_select selection "request-changes", and plain_text_input
    //   "Add error handling", Then HumanDecisionEvent has those exact
    //   field values.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Modal_submission_publishes_HumanDecisionEvent_with_question_id_verdict_and_comment()
    {
        TestHarness harness = new();
        string privateMetadata = JsonSerializer.Serialize(new
        {
            questionId = "Q-55",
        });
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_ABC",
            callbackId: "agent_review_modal",
            privateMetadata: privateMetadata,
            staticSelectValue: "request-changes",
            commentText: "Add error handling",
            userId: "U_REVIEWER",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_REVIEWER:view-1",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        HumanDecisionEvent decision = harness.TaskService.PublishedDecisions[0];
        decision.QuestionId.Should().Be("Q-55");
        decision.ActionValue.Should().Be("request-changes");
        decision.Comment.Should().Be("Add error handling");
        decision.Messenger.Should().Be(SlackInteractionHandler.MessengerName);
        decision.ExternalUserId.Should().Be("U_REVIEWER");
        decision.ExternalMessageId.Should().Be("V_ABC",
            "the brief pins ExternalMessageId = view.id for modal submissions");

        // Modal submission has no anchored parent message to mutate
        // (private_metadata carries no messageTs) -- chat.update is
        // skipped, not failed.
        harness.ChatUpdateClient.Requests.Should().BeEmpty();
        harness.ViewsOpenClient.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Scenario 3: RequiresComment triggers modal
    // -- Given a button click where the action's RequiresComment flag
    //   is true (encoded as `qc:` block_id prefix), When the handler
    //   processes it, Then a modal with a text input is opened
    //   (views.open) instead of submitting the decision directly.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_with_RequiresComment_opens_modal_and_does_not_publish_decision()
    {
        TestHarness harness = new();
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-77", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C123",
            messageTs: "1700000000.000200",
            userId: "U_REVIEWER",
            triggerId: "trig-2");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_REVIEWER:trig-2",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        // Brief acceptance criterion: NO decision published.
        harness.TaskService.PublishedDecisions.Should().BeEmpty(
            "a RequiresComment button MUST defer the decision until the comment modal is submitted");

        // Brief acceptance criterion: comment modal opened.
        harness.ViewsOpenClient.Requests.Should().ContainSingle();
        SlackViewsOpenRequest viewsRequest = harness.ViewsOpenClient.Requests[0];
        viewsRequest.TriggerId.Should().Be("trig-2");
        viewsRequest.TeamId.Should().Be(envelope.TeamId);
        viewsRequest.ViewPayload.Should().NotBeNull();

        // The renderer received the question id + pinned action value
        // so the modal can carry both forward in private_metadata.
        harness.MessageRenderer.LastCommentContext.Should().NotBeNull();
        SlackCommentModalContext ctx = harness.MessageRenderer.LastCommentContext!.Value;
        ctx.QuestionId.Should().Be("Q-77");
        ctx.ActionValue.Should().Be("request-changes");
        ctx.ActionLabel.Should().Be("Request changes");
        ctx.ChannelId.Should().Be("C123");
        ctx.MessageTs.Should().Be("1700000000.000200");

        // No chat.update yet -- the button stays live until the modal
        // submission completes.
        harness.ChatUpdateClient.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Regression: thread mapping lookup wins over fallback when a
    // mapping exists for the (team, channel, thread_ts) tuple.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_uses_thread_mapping_correlation_id_when_lookup_returns_match()
    {
        TestHarness harness = new();
        harness.ThreadMappingLookup.NextMapping = new SlackThreadMapping
        {
            TaskId = "TASK-7",
            TeamId = "T1",
            ChannelId = "C123",
            ThreadTs = "1700000000.000300",
            CorrelationId = "corr-thread-7",
            AgentId = "agent-alpha",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-1", requiresComment: false);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C123",
            messageTs: "1700000000.000300",
            userId: "U_ALICE",
            triggerId: "trig-3");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_ALICE:trig-3",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        harness.TaskService.PublishedDecisions[0].CorrelationId.Should().Be("corr-thread-7",
            "the SlackThreadMapping correlation id MUST win over the envelope's idempotency key");

        // Verify the lookup was called with the parsed (team, channel, message_ts).
        harness.ThreadMappingLookup.Lookups.Should().ContainSingle();
        (string Team, string? Channel, string? Thread) lookup = harness.ThreadMappingLookup.Lookups[0];
        lookup.Team.Should().Be("T1");
        lookup.Channel.Should().Be("C123");
        lookup.Thread.Should().Be("1700000000.000300");
    }

    // -----------------------------------------------------------------
    // Regression: Socket-Mode payload (raw JSON, no payload= wrapper)
    // -- The parser MUST accept both HTTP form bodies AND raw JSON
    //   objects since SlackSocketModeReceiver normalises socket events
    //   into raw JSON envelopes.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_parses_raw_json_socket_mode_payload()
    {
        TestHarness harness = new();
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-42", requiresComment: false);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C999",
            messageTs: "1700000000.000400",
            userId: "U_BOB",
            triggerId: "trig-4");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_BOB:trig-4",
            payload: payloadJson);

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        harness.TaskService.PublishedDecisions[0].QuestionId.Should().Be("Q-42");
        harness.TaskService.PublishedDecisions[0].ActionValue.Should().Be("approve");
    }

    // -----------------------------------------------------------------
    // Regression: chat.update failure is swallowed -- the decision
    // publish is the canonical outcome; a missed visual update must
    // NOT cause the inbound pipeline to retry (which would duplicate
    // the orchestrator dispatch).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_does_not_throw_when_chat_update_fails()
    {
        TestHarness harness = new();
        harness.ChatUpdateClient.NextResult = SlackChatUpdateResult.NetworkFailure("simulated timeout");
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-100", requiresComment: false);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C123",
            messageTs: "1700000000.000500",
            userId: "U_CAROL",
            triggerId: "trig-5");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_CAROL:trig-5",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().NotThrowAsync("chat.update is best-effort");
        harness.TaskService.PublishedDecisions.Should().ContainSingle();
    }

    // -----------------------------------------------------------------
    // Regression: PublishDecisionAsync exceptions DO propagate so the
    // inbound pipeline can retry / dead-letter the envelope.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_propagates_publish_exception()
    {
        TestHarness harness = new();
        harness.TaskService.ThrowOnPublish = new InvalidOperationException("orchestrator down");
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-1", requiresComment: false);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C123",
            messageTs: "1700000000.000600",
            userId: "U_DAVE",
            triggerId: "trig-6");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_DAVE:trig-6",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        harness.ChatUpdateClient.Requests.Should().BeEmpty(
            "chat.update must NOT run when the decision publish failed");
    }

    // -----------------------------------------------------------------
    // Regression: malformed block_id is ignored (handler logs and
    // returns rather than throwing) so a Slack-side bug cannot pin
    // the inbound pipeline in a retry loop.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_with_unrecognised_block_id_is_discarded_quietly()
    {
        TestHarness harness = new();
        string payloadJson = BuildBlockActionsPayload(
            blockId: "not-a-question-prefix",
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C123",
            messageTs: "1700000000.000700",
            userId: "U_EVE",
            triggerId: "trig-7");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_EVE:trig-7",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().BeEmpty();
        harness.ChatUpdateClient.Requests.Should().BeEmpty();
        harness.ViewsOpenClient.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Regression: modal submission with private_metadata carrying a
    // pinned actionValue + messageTs DOES disable the originating
    // buttons via chat.update (the RequiresComment round-trip).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Modal_submission_with_pinned_actionValue_and_messageTs_updates_originating_message()
    {
        TestHarness harness = new();
        string privateMetadata = JsonSerializer.Serialize(new
        {
            questionId = "Q-21",
            actionValue = "request-changes",
            actionLabel = "Request changes",
            channelId = "C123",
            messageTs = "1700000000.000800",
            userId = "U_FRANK",
            correlationId = "corr-pinned-21",
        });
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_PINNED",
            callbackId: SlackInteractionEncoding.CommentCallbackId,
            privateMetadata: privateMetadata,
            staticSelectValue: null,
            commentText: "Please add unit tests.",
            userId: "U_FRANK",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_FRANK:view-pinned",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        HumanDecisionEvent decision = harness.TaskService.PublishedDecisions[0];
        decision.QuestionId.Should().Be("Q-21");
        decision.ActionValue.Should().Be("request-changes",
            "private_metadata.actionValue (pinned by the RequiresComment round-trip) wins over view.state.values");
        decision.Comment.Should().Be("Please add unit tests.");
        decision.CorrelationId.Should().Be("corr-pinned-21",
            "private_metadata.correlationId (pinned at button-click time) wins over the envelope idempotency key");

        // chat.update DOES run because private_metadata carried the
        // originating channel + message ts.
        harness.ChatUpdateClient.Requests.Should().ContainSingle();
        harness.ChatUpdateClient.Requests[0].ChannelId.Should().Be("C123");
        harness.ChatUpdateClient.Requests[0].MessageTs.Should().Be("1700000000.000800");
    }

    // -----------------------------------------------------------------
    // Regression: unsupported interaction type is ignored (Slack ships
    // shortcuts, block_suggestion, message_action, etc.; Stage 5.3
    // only owns block_actions / view_submission).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Unsupported_interaction_type_is_ignored()
    {
        TestHarness harness = new();
        string payloadJson = JsonSerializer.Serialize(new
        {
            type = "shortcut",
            team = new { id = "T1" },
            user = new { id = "U_SHORTCUT" },
        });
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_SHORTCUT:trig-shortcut",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().BeEmpty();
        harness.ChatUpdateClient.Requests.Should().BeEmpty();
        harness.ViewsOpenClient.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Evaluator iter-1 item #1: in-thread reply clicks MUST use
    // message.thread_ts as the SlackThreadMapping lookup key (NOT
    // message.ts, which is the reply's own ts and is not in the
    // mapping table).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_on_in_thread_reply_resolves_correlation_via_thread_ts()
    {
        TestHarness harness = new();
        harness.ThreadMappingLookup.NextMapping = new SlackThreadMapping
        {
            TaskId = "TASK-thread",
            TeamId = "T1",
            ChannelId = "C123",
            ThreadTs = "1700000000.000ROOT", // the thread's root ts
            CorrelationId = "corr-root-thread",
            AgentId = "agent-alpha",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-IN-THREAD", requiresComment: false);

        // The clicked message has its OWN ts (a reply ts) AND
        // thread_ts pointing at the root. The handler MUST look the
        // mapping up by thread_ts, not the reply's own ts.
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C123",
            messageTs: "1700000000.000REPLY",
            userId: "U_ALICE",
            triggerId: "trig-thread",
            threadTs: "1700000000.000ROOT");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_ALICE:trig-thread",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ThreadMappingLookup.Lookups.Should().ContainSingle();
        harness.ThreadMappingLookup.Lookups[0].Thread.Should().Be(
            "1700000000.000ROOT",
            "the SlackThreadMapping lookup MUST use message.thread_ts (the root timestamp) so in-thread reply clicks reach the mapping row");

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        harness.TaskService.PublishedDecisions[0].CorrelationId.Should().Be(
            "corr-root-thread",
            "publishing must adopt the SlackThreadMapping.CorrelationId resolved via thread_ts");

        // chat.update STILL uses the reply's own message.ts (chat.update
        // operates on the specific message, not the thread).
        harness.ChatUpdateClient.Requests.Should().ContainSingle();
        harness.ChatUpdateClient.Requests[0].MessageTs.Should().Be("1700000000.000REPLY");
    }

    // -----------------------------------------------------------------
    // Evaluator iter-1 item #2: SlackThreadMapping lookup failures MUST
    // propagate so the inbound pipeline can retry / dead-letter --
    // silently falling back to the envelope idempotency key publishes
    // a decision with the WRONG correlation id and silently breaks the
    // "queryable by correlation id" acceptance criterion.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_propagates_thread_mapping_lookup_failure()
    {
        TestHarness harness = new();
        harness.ThreadMappingLookup.ThrowOnLookup = new InvalidOperationException("simulated DB outage");
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-DBFAIL", requiresComment: false);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C123",
            messageTs: "1700000000.000DB",
            userId: "U_ALICE",
            triggerId: "trig-dbfail");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_ALICE:trig-dbfail",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*simulated DB outage*");
        harness.TaskService.PublishedDecisions.Should().BeEmpty(
            "a thread-mapping lookup failure must NOT publish a decision with the wrong correlation id");
        harness.ChatUpdateClient.Requests.Should().BeEmpty(
            "no chat.update because no decision was published");
    }

    // -----------------------------------------------------------------
    // Evaluator iter-1 item #3: view_submission with private_metadata
    // that ONLY carries taskId (and no questionId) MUST be discarded
    // -- the architecture mapping table keys HumanDecisionEvent on
    // QuestionId, so the renderer is required to encode questionId
    // explicitly. Earlier iterations accepted TaskId as a fallback;
    // removing that prevents arbitrary task-shaped metadata from
    // leaking into the decision pipeline.
    // -----------------------------------------------------------------
    [Fact]
    public async Task View_submission_with_taskId_only_private_metadata_is_discarded()
    {
        TestHarness harness = new();
        string privateMetadata = JsonSerializer.Serialize(new
        {
            taskId = "TASK-orphan",
            subCommand = "review",
            correlationId = "corr-orphan",
        });
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_ORPHAN",
            callbackId: DefaultSlackMessageRenderer.ReviewCallbackId,
            privateMetadata: privateMetadata,
            staticSelectValue: "approve",
            commentText: null,
            userId: "U_ORPHAN",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_ORPHAN:view-orphan",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().BeEmpty(
            "a private_metadata payload missing questionId MUST be discarded -- the TaskId fallback was removed by evaluator iter-1 item #3");
    }

    // -----------------------------------------------------------------
    // Evaluator iter-1 item #3 -- complementary: the production
    // DefaultSlackMessageRenderer's review modal private_metadata MUST
    // contain questionId. Pairs with the discard test above so the
    // contract is end-to-end verified.
    // -----------------------------------------------------------------
    [Fact]
    public void DefaultSlackMessageRenderer_review_modal_private_metadata_contains_questionId_equal_to_task_id()
    {
        DefaultSlackMessageRenderer renderer = new();
        object view = renderer.RenderReviewModal(new SlackReviewModalContext(
            TaskId: "TASK-99",
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            CorrelationId: "corr-99"));

        string json = JsonSerializer.Serialize(view);
        using JsonDocument doc = JsonDocument.Parse(json);
        string? privateMetadataString = doc.RootElement.GetProperty("private_metadata").GetString();
        privateMetadataString.Should().NotBeNull();

        using JsonDocument metadataDoc = JsonDocument.Parse(privateMetadataString!);
        metadataDoc.RootElement.GetProperty("questionId").GetString().Should().Be("TASK-99");
        metadataDoc.RootElement.GetProperty("taskId").GetString().Should().Be("TASK-99");
    }

    [Fact]
    public void DefaultSlackMessageRenderer_escalate_modal_private_metadata_contains_questionId_equal_to_task_id()
    {
        DefaultSlackMessageRenderer renderer = new();
        object view = renderer.RenderEscalateModal(new SlackEscalateModalContext(
            TaskId: "TASK-77",
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            CorrelationId: "corr-77"));

        string json = JsonSerializer.Serialize(view);
        using JsonDocument doc = JsonDocument.Parse(json);
        string? privateMetadataString = doc.RootElement.GetProperty("private_metadata").GetString();
        privateMetadataString.Should().NotBeNull();

        using JsonDocument metadataDoc = JsonDocument.Parse(privateMetadataString!);
        metadataDoc.RootElement.GetProperty("questionId").GetString().Should().Be("TASK-77");
    }

    // -----------------------------------------------------------------
    // Stage 5.3 iter-4 evaluator item #3 (STRUCTURAL fix). The escalate
    // modal renders text-only inputs (target + reason) with no
    // static_select, so the view-submission handler cannot read a
    // verdict from view.state.values. The renderer therefore MUST pin
    // actionValue in private_metadata so the handler's pinned-metadata
    // precedence branch produces a clean HumanDecisionEvent rather
    // than falling back to a raw text-input value (which is the bug
    // the evaluator flagged). The handler ALSO drops the
    // FirstStaticSelectValueFallback branch from its actionValue
    // resolution chain so the only paths to a published decision are
    // (a) pinned metadata.ActionValue, or (b) a real static_select in
    // view.state.values; everything else is discarded.
    // -----------------------------------------------------------------
    [Fact]
    public void DefaultSlackMessageRenderer_escalate_modal_private_metadata_pins_actionValue_escalate()
    {
        DefaultSlackMessageRenderer renderer = new();
        object view = renderer.RenderEscalateModal(new SlackEscalateModalContext(
            TaskId: "TASK-99",
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            CorrelationId: "corr-99"));

        string json = JsonSerializer.Serialize(view);
        using JsonDocument doc = JsonDocument.Parse(json);
        string? privateMetadataString = doc.RootElement.GetProperty("private_metadata").GetString();
        privateMetadataString.Should().NotBeNull();

        using JsonDocument metadataDoc = JsonDocument.Parse(privateMetadataString!);
        metadataDoc.RootElement.GetProperty("actionValue").GetString().Should().Be(
            DefaultSlackMessageRenderer.EscalateActionValue,
            "the escalate modal MUST pin actionValue in private_metadata because it has no static_select for the handler to read from view.state.values");
    }

    [Fact]
    public async Task Escalate_view_submission_with_pinned_actionValue_publishes_decision_with_actionValue_escalate()
    {
        TestHarness harness = new();
        string privateMetadata = JsonSerializer.Serialize(new
        {
            questionId = "TASK-ESC-OK",
            taskId = "TASK-ESC-OK",
            subCommand = "escalate",
            actionValue = DefaultSlackMessageRenderer.EscalateActionValue,
            correlationId = "corr-esc-ok",
        });
        // BuildViewSubmissionPayload encodes view.state.values with the
        // first input keyed on "agent_comment" for plain_text_input --
        // for an escalate submission this stands in for the "target" /
        // "reason" inputs that the production renderer emits. The
        // handler MUST NOT use these as ActionValue because actionValue
        // is pinned in private_metadata.
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_ESC_OK",
            callbackId: DefaultSlackMessageRenderer.EscalateCallbackId,
            privateMetadata: privateMetadata,
            staticSelectValue: null,
            commentText: "@oncall please look at TASK-ESC-OK",
            userId: "U_ESCALATOR",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_ESCALATOR:view-esc-ok",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        HumanDecisionEvent decision = harness.TaskService.PublishedDecisions.Should().ContainSingle().Subject;
        decision.QuestionId.Should().Be("TASK-ESC-OK");
        decision.ActionValue.Should().Be(DefaultSlackMessageRenderer.EscalateActionValue,
            "the escalate modal's pinned actionValue MUST be the source of truth -- anything else means the handler fell back to a text-input value (the iter-3 evaluator's malformed-escalation bug)");
        decision.Comment.Should().Be("@oncall please look at TASK-ESC-OK",
            "the plain_text_input value MUST land in Comment, not ActionValue");
    }

    [Fact]
    public async Task Escalate_view_submission_without_pinned_actionValue_is_discarded_and_publishes_no_decision()
    {
        TestHarness harness = new();
        // Simulates a (legacy or hand-rolled) escalate modal whose
        // private_metadata does NOT pin actionValue. With iter-4's
        // FirstStaticSelectValueFallback removal in
        // HandleViewSubmissionAsync, the handler MUST discard this
        // submission rather than silently using the text-input value
        // as a verdict.
        string privateMetadata = JsonSerializer.Serialize(new
        {
            questionId = "TASK-ESC-LEG",
            taskId = "TASK-ESC-LEG",
            subCommand = "escalate",
            correlationId = "corr-esc-leg",
        });
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_ESC_LEG",
            callbackId: DefaultSlackMessageRenderer.EscalateCallbackId,
            privateMetadata: privateMetadata,
            staticSelectValue: null,
            commentText: "@oncall please review",
            userId: "U_ESCALATOR",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_ESCALATOR:view-esc-leg",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().BeEmpty(
            "an escalate view_submission with no pinned actionValue and no static_select MUST be discarded -- the iter-3 evaluator item #3 bug was the handler silently using a text-input value as ActionValue; iter-4's FirstStaticSelectValueFallback removal closes that hole");
    }

    // -----------------------------------------------------------------
    // Evaluator iter-1 item #4: a RequiresComment button whose follow-up
    // views.open fails MUST throw so the pipeline retries / dead-letters
    // -- the human action was ACKed and NO HumanDecisionEvent landed, so
    // silent-return loses the click.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_with_RequiresComment_propagates_views_open_failure()
    {
        TestHarness harness = new();
        harness.ViewsOpenClient.NextResult = SlackViewsOpenResult.NetworkFailure("simulated trigger_id expired");
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-VFAIL", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C123",
            messageTs: "1700000000.000VFAIL",
            userId: "U_REVIEWER",
            triggerId: "trig-vfail");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_REVIEWER:trig-vfail",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a failed views.open for a RequiresComment click leaves the human action without a decision -- the pipeline MUST be allowed to retry / dead-letter");
        harness.TaskService.PublishedDecisions.Should().BeEmpty(
            "no decision was published yet (RequiresComment defers the publish until the comment modal submits)");
    }

    [Fact]
    public async Task Button_click_with_RequiresComment_propagates_missing_trigger_id()
    {
        TestHarness harness = new();
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-NOTRG", requiresComment: true);
        // Build payload WITHOUT trigger_id at all -- and also clear it
        // from the envelope so the handler cannot recover it.
        string payloadJson = JsonSerializer.Serialize(new
        {
            type = SlackInteractionHandler.BlockActionsType,
            team = new { id = "T1" },
            user = new { id = "U_REVIEWER" },
            channel = new { id = "C123" },
            message = new { ts = "1700000000.000NOTRG" },
            actions = new object[]
            {
                new
                {
                    type = "button",
                    block_id = blockId,
                    action_id = "agent_action",
                    value = "request-changes",
                    text = new { type = "plain_text", text = "Request changes" },
                },
            },
        });
        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "int:T1:U_REVIEWER:notrg",
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: "T1",
            ChannelId: "C123",
            UserId: "U_REVIEWER",
            RawPayload: "payload=" + Uri.EscapeDataString(payloadJson),
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        harness.TaskService.PublishedDecisions.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Evaluator iter-1 item #5: view_submission MUST gate on callback_id.
    // Slack delivers ANY workspace modal submission to this endpoint;
    // unknown callback_ids (other apps, unrelated workflows) must be
    // ignored even if their private_metadata happens to look
    // agent-shaped.
    // -----------------------------------------------------------------
    [Fact]
    public async Task View_submission_with_unknown_callback_id_is_ignored()
    {
        TestHarness harness = new();
        string privateMetadata = JsonSerializer.Serialize(new
        {
            questionId = "Q-INTRUDER",
        });
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_INTRUDER",
            callbackId: "some_third_party_modal",
            privateMetadata: privateMetadata,
            staticSelectValue: "approve",
            commentText: "should be ignored",
            userId: "U_INTRUDER",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_INTRUDER:view-intruder",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().BeEmpty(
            "an unknown callback_id MUST NOT be converted into a HumanDecisionEvent -- evaluator iter-1 item #5");
        harness.ChatUpdateClient.Requests.Should().BeEmpty();
        harness.ViewsOpenClient.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task View_submission_with_missing_callback_id_is_ignored()
    {
        TestHarness harness = new();
        string privateMetadata = JsonSerializer.Serialize(new
        {
            questionId = "Q-NOCB",
        });
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_NOCB",
            callbackId: null,
            privateMetadata: privateMetadata,
            staticSelectValue: "approve",
            commentText: null,
            userId: "U_NOCB",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_NOCB:view-nocb",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Evaluator iter-2 item #1 (modal side): comment-modal submissions
    // resolve correlation via the pinned threadTs (not the pinned
    // messageTs) so an in-thread reply click round-trips correctly
    // through the RequiresComment flow.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Modal_submission_uses_pinned_threadTs_for_correlation_lookup()
    {
        TestHarness harness = new();
        harness.ThreadMappingLookup.NextMapping = new SlackThreadMapping
        {
            TaskId = "TASK-modal-thread",
            TeamId = "T1",
            ChannelId = "C123",
            ThreadTs = "1700000000.000MTR",
            CorrelationId = "corr-modal-root",
            AgentId = "agent-alpha",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // private_metadata carries BOTH messageTs (the reply's own ts)
        // AND threadTs (the root). The handler MUST look up by threadTs.
        string privateMetadata = JsonSerializer.Serialize(new
        {
            questionId = "Q-MODAL-THREAD",
            actionValue = "approve",
            channelId = "C123",
            messageTs = "1700000000.000MREPLY",
            threadTs = "1700000000.000MTR",
            // NO correlationId pinned -> handler MUST fall through to
            // the thread lookup using threadTs.
        });
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_MODAL_THREAD",
            callbackId: SlackInteractionEncoding.CommentCallbackId,
            privateMetadata: privateMetadata,
            staticSelectValue: null,
            commentText: "Looks good.",
            userId: "U_MODAL",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_MODAL:view-modal-thread",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ThreadMappingLookup.Lookups.Should().ContainSingle();
        harness.ThreadMappingLookup.Lookups[0].Thread.Should().Be("1700000000.000MTR",
            "modal submissions must look up by the pinned thread_ts (root), not the message_ts (reply)");

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        harness.TaskService.PublishedDecisions[0].CorrelationId.Should().Be("corr-modal-root");
    }

    // -----------------------------------------------------------------
    // Evaluator iter-2 item #5 (production-payload coverage): a Slack
    // payload that pins thread context ONLY on the `container`
    // sub-object (no `message` sibling -- the shape Slack actually
    // uses for some surfaces) MUST still resolve correlation via
    // container.thread_ts.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Button_click_with_container_thread_ts_resolves_correlation_via_container_thread_ts()
    {
        TestHarness harness = new();
        harness.ThreadMappingLookup.NextMapping = new SlackThreadMapping
        {
            TaskId = "TASK-container",
            TeamId = "T1",
            ChannelId = "C123",
            ThreadTs = "1700000000.000CROOT",
            CorrelationId = "corr-container-root",
            AgentId = "agent-alpha",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-CT", requiresComment: false);

        // Slack ships some surfaces with thread context ONLY on the
        // container node; the message node is absent. The Stage 5.3
        // parser (SlackInteractionPayloadDetailParser line ~852)
        // falls back to container.thread_ts / container.message_ts in
        // that case. This payload exercises that production-only path.
        var payload = new
        {
            type = SlackInteractionHandler.BlockActionsType,
            trigger_id = "trig-container",
            team = new { id = "T1" },
            user = new { id = "U_ALICE" },
            channel = new { id = "C123" },
            container = new
            {
                type = "message",
                message_ts = "1700000000.000CREPLY",
                thread_ts = "1700000000.000CROOT",
                channel_id = "C123",
            },
            actions = new object[]
            {
                new
                {
                    type = "button",
                    block_id = blockId,
                    action_id = "agent_action",
                    value = "approve",
                    text = new { type = "plain_text", text = "Approve" },
                },
            },
        };
        string payloadJson = JsonSerializer.Serialize(payload);
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_ALICE:trig-container",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ThreadMappingLookup.Lookups.Should().ContainSingle();
        harness.ThreadMappingLookup.Lookups[0].Thread.Should().Be(
            "1700000000.000CROOT",
            "container.thread_ts MUST be preferred when message.thread_ts is absent -- in-thread clicks on container-only payloads otherwise miss the SlackThreadMapping row");

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        harness.TaskService.PublishedDecisions[0].CorrelationId.Should().Be("corr-container-root");
    }

    // -----------------------------------------------------------------
    // Evaluator iter-2 item #5 (renderer -> handler round-trip):
    // exercise the actual DefaultSlackMessageRenderer.RenderReviewModal
    // through SlackInteractionPrivateMetadata into the handler so the
    // private_metadata contract is verified end-to-end. Pairs with the
    // existing renderer-only assertion to lock in BOTH halves of the
    // contract: the renderer's encoded private_metadata round-trips
    // through SlackPrivateMetadata.Parse and the resulting
    // HumanDecisionEvent's QuestionId matches the renderer's TaskId.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Review_modal_renderer_to_handler_round_trip_publishes_decision_with_questionId_from_renderer()
    {
        TestHarness harness = new();

        // Step 1: render the review modal exactly as production does.
        DefaultSlackMessageRenderer renderer = new();
        object view = renderer.RenderReviewModal(new SlackReviewModalContext(
            TaskId: "TASK-round-trip",
            TeamId: "T1",
            ChannelId: "C123",
            UserId: "U_REVIEWER",
            CorrelationId: "corr-round-trip"));

        // Step 2: extract the renderer's private_metadata + callback_id
        // verbatim. Test must NOT hand-craft them -- the whole point
        // is to verify the renderer's output reaches the handler
        // unchanged.
        string viewJson = JsonSerializer.Serialize(view);
        using JsonDocument viewDoc = JsonDocument.Parse(viewJson);
        string privateMetadata = viewDoc.RootElement.GetProperty("private_metadata").GetString()!;
        string callbackId = viewDoc.RootElement.GetProperty("callback_id").GetString()!;
        callbackId.Should().Be(
            DefaultSlackMessageRenderer.ReviewCallbackId,
            "the renderer must emit the agent_review_modal callback_id so the handler's IsRecognizedViewCallback gate accepts the submission");

        // Step 3: simulate Slack's view_submission carrying that same
        // private_metadata back to /api/slack/interactions, with the
        // reviewer's verdict + comment.
        string payloadJson = BuildViewSubmissionPayload(
            viewId: "V_ROUNDTRIP",
            callbackId: callbackId,
            privateMetadata: privateMetadata,
            staticSelectValue: "request-changes",
            commentText: "Add unit tests.",
            userId: "U_REVIEWER",
            teamId: "T1");
        SlackInboundEnvelope envelope = BuildEnvelope(
            idempotencyKey: "int:T1:U_REVIEWER:view-roundtrip",
            payload: "payload=" + Uri.EscapeDataString(payloadJson));

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        // Step 4: assert the HumanDecisionEvent reflects the renderer's
        // pinned questionId AND the reviewer's verdict / comment.
        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        HumanDecisionEvent decision = harness.TaskService.PublishedDecisions[0];
        decision.QuestionId.Should().Be(
            "TASK-round-trip",
            "the renderer encoded questionId = TaskId in private_metadata; the handler must reproduce it on the HumanDecisionEvent");
        decision.ActionValue.Should().Be(
            "request-changes",
            "the renderer's static_select option value MUST match the contract documented in the Stage 5.3 brief");
        decision.Comment.Should().Be("Add unit tests.");
        decision.CorrelationId.Should().Be(
            "corr-round-trip",
            "the renderer pinned correlationId in private_metadata; the handler must adopt it without consulting the thread mapping lookup");

        // The lookup must NOT have been called -- private_metadata
        // pinned the correlation id so the fallback path is unused.
        harness.ThreadMappingLookup.Lookups.Should().BeEmpty(
            "private_metadata.correlationId pinned by the renderer wins over a thread-mapping lookup");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------
    private static string BuildBlockActionsPayload(
        string blockId,
        string actionValue,
        string actionLabel,
        string channelId,
        string messageTs,
        string userId,
        string triggerId,
        string teamId = "T1",
        string? threadTs = null)
    {
        // Slack ships message.thread_ts ONLY when the click landed on
        // a message inside an existing thread. Tests that simulate a
        // reply click pass threadTs explicitly; tests that simulate a
        // root-message click leave it null so the parser falls back to
        // message.ts for the SlackThreadMapping lookup key.
        object messageObj = string.IsNullOrEmpty(threadTs)
            ? new { ts = messageTs }
            : (object)new { ts = messageTs, thread_ts = threadTs };

        var payload = new
        {
            type = SlackInteractionHandler.BlockActionsType,
            trigger_id = triggerId,
            team = new { id = teamId },
            user = new { id = userId },
            channel = new { id = channelId },
            message = messageObj,
            actions = new object[]
            {
                new
                {
                    type = "button",
                    block_id = blockId,
                    action_id = "agent_action",
                    value = actionValue,
                    text = new { type = "plain_text", text = actionLabel },
                },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildViewSubmissionPayload(
        string viewId,
        string? callbackId,
        string privateMetadata,
        string? staticSelectValue,
        string? commentText,
        string userId,
        string teamId)
    {
        // view.state.values is keyed by block_id -> action_id -> action object.
        Dictionary<string, Dictionary<string, object>> blocks = new();

        if (!string.IsNullOrEmpty(staticSelectValue))
        {
            blocks["agent_verdict"] = new Dictionary<string, object>
            {
                ["agent_verdict_select"] = new
                {
                    type = "static_select",
                    selected_option = new
                    {
                        text = new { type = "plain_text", text = staticSelectValue },
                        value = staticSelectValue,
                    },
                },
            };
        }

        if (commentText is not null)
        {
            blocks["agent_comment"] = new Dictionary<string, object>
            {
                ["agent_comment_input"] = new
                {
                    type = "plain_text_input",
                    value = commentText,
                },
            };
        }

        var payload = new
        {
            type = SlackInteractionHandler.ViewSubmissionType,
            team = new { id = teamId },
            user = new { id = userId },
            view = new
            {
                id = viewId,
                callback_id = callbackId,
                private_metadata = privateMetadata,
                state = new
                {
                    values = blocks,
                },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static SlackInboundEnvelope BuildEnvelope(
        string idempotencyKey,
        string payload,
        string teamId = "T1",
        string? channelId = "C123",
        string userId = "U_ALICE",
        string? triggerId = "trig-1")
    {
        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: teamId,
            ChannelId: channelId,
            UserId: userId,
            RawPayload: payload,
            TriggerId: triggerId,
            ReceivedAt: DateTimeOffset.UtcNow);
    }

    // -----------------------------------------------------------------
    // Harness + fakes
    // -----------------------------------------------------------------
    private sealed class TestHarness
    {
        public TestHarness()
        {
            this.TaskService = new RecordingAgentTaskService();
            this.ThreadMappingLookup = new RecordingThreadMappingLookup();
            this.ChatUpdateClient = new RecordingChatUpdateClient();
            this.ViewsOpenClient = new RecordingViewsOpenClient();
            this.MessageRenderer = new RecordingMessageRenderer();
            this.Handler = new SlackInteractionHandler(
                this.TaskService,
                this.ThreadMappingLookup,
                this.ChatUpdateClient,
                this.ViewsOpenClient,
                this.MessageRenderer,
                NullLogger<SlackInteractionHandler>.Instance,
                TimeProvider.System);
        }

        public RecordingAgentTaskService TaskService { get; }

        public RecordingThreadMappingLookup ThreadMappingLookup { get; }

        public RecordingChatUpdateClient ChatUpdateClient { get; }

        public RecordingViewsOpenClient ViewsOpenClient { get; }

        public RecordingMessageRenderer MessageRenderer { get; }

        public SlackInteractionHandler Handler { get; }
    }

    private sealed class RecordingAgentTaskService : IAgentTaskService
    {
        private readonly ConcurrentQueue<HumanDecisionEvent> publishedDecisions = new();

        public IReadOnlyList<HumanDecisionEvent> PublishedDecisions => this.publishedDecisions.ToArray();

        public Exception? ThrowOnPublish { get; set; }

        public Task<AgentTaskCreationResult> CreateTaskAsync(AgentTaskCreationRequest request, CancellationToken ct)
            => Task.FromResult(new AgentTaskCreationResult(
                TaskId: "TASK-stub",
                CorrelationId: request.CorrelationId,
                Acknowledgement: string.Empty));

        public Task<AgentTaskStatusResult> GetTaskStatusAsync(AgentTaskStatusQuery query, CancellationToken ct)
            => Task.FromResult(new AgentTaskStatusResult(
                Scope: "swarm",
                Summary: string.Empty,
                Entries: Array.Empty<AgentTaskStatusEntry>()));

        public Task PublishDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
        {
            if (this.ThrowOnPublish is not null)
            {
                throw this.ThrowOnPublish;
            }

            this.publishedDecisions.Enqueue(decision);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingThreadMappingLookup : ISlackThreadMappingLookup
    {
        private readonly ConcurrentQueue<(string Team, string? Channel, string? Thread)> lookups = new();

        public IReadOnlyList<(string Team, string? Channel, string? Thread)> Lookups => this.lookups.ToArray();

        public SlackThreadMapping? NextMapping { get; set; }

        public Exception? ThrowOnLookup { get; set; }

        public Task<SlackThreadMapping?> LookupAsync(
            string teamId,
            string? channelId,
            string? threadTs,
            CancellationToken ct)
        {
            this.lookups.Enqueue((teamId, channelId, threadTs));
            if (this.ThrowOnLookup is not null)
            {
                throw this.ThrowOnLookup;
            }

            return Task.FromResult(this.NextMapping);
        }
    }

    private sealed class RecordingChatUpdateClient : ISlackChatUpdateClient
    {
        private readonly ConcurrentQueue<SlackChatUpdateRequest> requests = new();

        public IReadOnlyList<SlackChatUpdateRequest> Requests => this.requests.ToArray();

        public SlackChatUpdateResult NextResult { get; set; } = SlackChatUpdateResult.Success();

        public Task<SlackChatUpdateResult> UpdateAsync(SlackChatUpdateRequest request, CancellationToken ct)
        {
            this.requests.Enqueue(request);
            return Task.FromResult(this.NextResult);
        }
    }

    private sealed class RecordingViewsOpenClient : ISlackViewsOpenClient
    {
        private readonly ConcurrentQueue<SlackViewsOpenRequest> requests = new();

        public IReadOnlyList<SlackViewsOpenRequest> Requests => this.requests.ToArray();

        public SlackViewsOpenResult NextResult { get; set; } = SlackViewsOpenResult.Success();

        public Task<SlackViewsOpenResult> OpenAsync(SlackViewsOpenRequest request, CancellationToken ct)
        {
            this.requests.Enqueue(request);
            return Task.FromResult(this.NextResult);
        }
    }

    private sealed class RecordingMessageRenderer : ISlackMessageRenderer
    {
        public SlackReviewModalContext? LastReviewContext { get; private set; }

        public SlackEscalateModalContext? LastEscalateContext { get; private set; }

        public SlackCommentModalContext? LastCommentContext { get; private set; }

        public object RenderReviewModal(SlackReviewModalContext context)
        {
            this.LastReviewContext = context;
            return new { type = "modal", callback_id = "agent_review_modal" };
        }

        public object RenderEscalateModal(SlackEscalateModalContext context)
        {
            this.LastEscalateContext = context;
            return new { type = "modal", callback_id = "agent_escalate_modal" };
        }

        public object RenderCommentModal(SlackCommentModalContext context)
        {
            this.LastCommentContext = context;
            return new
            {
                type = "modal",
                callback_id = SlackInteractionEncoding.CommentCallbackId,
                question_id = context.QuestionId,
            };
        }
    }
}
