// -----------------------------------------------------------------------
// <copyright file="SlackAppMentionHandlerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 5.2 brief-mandated tests for
/// <see cref="SlackAppMentionHandler"/>. Covers the two test scenarios
/// in <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (App Mention Processing) plus regression coverage for thread-anchor
/// selection, the "no trigger_id" review/escalate fall-back, and the
/// unrecognised sub-command path.
/// </summary>
public sealed class SlackAppMentionHandlerTests
{
    // -----------------------------------------------------------------
    // Brief scenario 1: App mention with text "<@U123> ask design
    // persistence layer" dispatches `ask` with prompt
    // "design persistence layer".
    // -----------------------------------------------------------------
    [Fact]
    public async Task Ask_app_mention_dispatches_CreateTaskAsync_with_prompt_text_after_stripping_bot_prefix()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-ask-1",
            text: "<@U123> ask design persistence layer");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().ContainSingle();
        AgentTaskCreationRequest request = harness.TaskService.CreateRequests[0];
        request.Prompt.Should().Be("design persistence layer",
            "the bot mention prefix MUST be stripped and the remainder used verbatim as the prompt");
        request.Messenger.Should().Be(SlackCommandHandler.MessengerName);
        request.ExternalUserId.Should().Be("U1");
        request.ChannelId.Should().Be("C1");
        request.CorrelationId.Should().Be(envelope.IdempotencyKey);

        // The brief requires a threaded reply, not an ephemeral
        // response_url POST.
        harness.ThreadedPoster.Replies.Should().ContainSingle();
        SlackThreadedReplyRequest reply = harness.ThreadedPoster.Replies[0];
        reply.TeamId.Should().Be("T1");
        reply.ChannelId.Should().Be("C1");
    }

    // -----------------------------------------------------------------
    // Brief scenario 2: Bot ID stripping for "<@U123BOT> status TASK-42"
    // yields sub-command `status` with argument `TASK-42`.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Status_app_mention_strips_arbitrary_bot_id_and_queries_orchestrator_with_task_id()
    {
        TestHarness harness = new();
        harness.TaskService.NextStatusResult = new AgentTaskStatusResult(
            Scope: "task",
            Summary: "Task TASK-42 is running.",
            Entries: new[] { new AgentTaskStatusEntry("TASK-42", "running", "writing plan") });

        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-status-1",
            text: "<@U123BOT> status TASK-42");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.StatusQueries.Should().ContainSingle();
        AgentTaskStatusQuery query = harness.TaskService.StatusQueries[0];
        query.TaskId.Should().Be("TASK-42",
            "the bot id strip MUST be permissive about the inner id format (U123BOT, not just numeric U-codes)");
        query.Messenger.Should().Be(SlackCommandHandler.MessengerName);
        query.CorrelationId.Should().Be(envelope.IdempotencyKey);

        harness.ThreadedPoster.Replies.Should().ContainSingle()
            .Which.Text.Should().Contain("TASK-42").And.Contain("running");
    }

    [Fact]
    public async Task Bot_prefix_with_display_name_suffix_is_stripped_so_named_mentions_still_parse()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-ask-named",
            text: "<@U123BOT|agentbot> ask plan persistence failover");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().ContainSingle()
            .Which.Prompt.Should().Be("plan persistence failover");
    }

    // -----------------------------------------------------------------
    // Thread routing: when the mention is posted INSIDE a thread the
    // handler routes the reply to event.thread_ts; when the mention is
    // a top-level channel post the handler anchors a new thread on
    // event.ts.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Mention_inside_existing_thread_routes_reply_to_thread_ts()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-thread",
            text: "<@U123> approve Q-9",
            ts: "1700000000.000200",
            threadTs: "1700000000.000100");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ThreadedPoster.Replies.Should().ContainSingle();
        harness.ThreadedPoster.Replies[0].ThreadTs.Should().Be("1700000000.000100",
            "an in-thread mention MUST reply to the existing thread anchor, not the mention's own ts");
    }

    [Fact]
    public async Task Top_level_mention_anchors_new_thread_on_event_ts()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-toplevel",
            text: "<@U123> approve Q-7",
            ts: "1700000000.123456",
            threadTs: null);

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ThreadedPoster.Replies.Should().ContainSingle();
        harness.ThreadedPoster.Replies[0].ThreadTs.Should().Be("1700000000.123456",
            "a top-level mention MUST anchor a NEW thread on the mention's own ts so Slack promotes the reply into a thread instead of posting at the channel root");
    }

    // -----------------------------------------------------------------
    // Unrecognised sub-command: same dispatch path as slash command =>
    // usage hint is rendered, but delivered as a threaded reply (not
    // ephemeral).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Unknown_sub_command_replies_usage_hint_as_threaded_reply()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-unknown",
            text: "<@U123> shrug something");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().BeEmpty();
        harness.TaskService.PublishedDecisions.Should().BeEmpty();
        harness.TaskService.StatusQueries.Should().BeEmpty();

        harness.ThreadedPoster.Replies.Should().ContainSingle();
        string body = harness.ThreadedPoster.Replies[0].Text;
        body.Should().Contain("shrug");
        body.Should().Contain("ask")
            .And.Contain("status")
            .And.Contain("approve")
            .And.Contain("reject")
            .And.Contain("review")
            .And.Contain("escalate");
    }

    [Fact]
    public async Task Empty_mention_text_after_prefix_strip_replies_missing_sub_command_usage()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-empty",
            text: "<@U123>");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().BeEmpty();
        harness.ThreadedPoster.Replies.Should().ContainSingle()
            .Which.Text.Should().Contain("Missing sub-command");
    }

    [Fact]
    public async Task Ask_without_prompt_replies_usage_error_as_threaded_reply()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-ask-bare",
            text: "<@U123> ask");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().BeEmpty();
        harness.ThreadedPoster.Replies.Should().ContainSingle()
            .Which.Text.Should().Contain("`/agent ask` requires a prompt");
    }

    [Fact]
    public async Task Approve_app_mention_publishes_HumanDecisionEvent_with_approve_action()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-approve",
            text: "<@U123> approve Q-123");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        HumanDecisionEvent decision = harness.TaskService.PublishedDecisions[0];
        decision.QuestionId.Should().Be("Q-123");
        decision.ActionValue.Should().Be(SlackCommandHandler.ApproveActionValue);
        decision.Messenger.Should().Be(SlackCommandHandler.MessengerName);
        decision.ExternalUserId.Should().Be("U1");
        decision.CorrelationId.Should().Be(envelope.IdempotencyKey);

        harness.ThreadedPoster.Replies.Should().ContainSingle()
            .Which.Text.Should().Contain("approve").And.Contain("Q-123");
    }

    // -----------------------------------------------------------------
    // Review / escalate via app_mention: there is no trigger_id in the
    // Events API payload, so the handler MUST fall through the
    // missing-trigger-id branch and surface a threaded hint (per
    // e2e-scenarios.md Feature 7 sub-scenarios 7.2 / 7.3).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Review_via_app_mention_falls_back_with_threaded_hint_because_no_trigger_id_is_available()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-review",
            text: "<@U123> review TASK-42");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ViewsOpenClient.Requests.Should().BeEmpty(
            "Events API app_mention payloads do not carry a trigger_id, so views.open MUST NOT be attempted");

        harness.ThreadedPoster.Replies.Should().ContainSingle();
        harness.ThreadedPoster.Replies[0].Text.Should().Contain("trigger_id is missing");
    }

    [Fact]
    public async Task Escalate_via_app_mention_falls_back_with_threaded_hint_because_no_trigger_id_is_available()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-escalate",
            text: "<@U123> escalate TASK-9");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ViewsOpenClient.Requests.Should().BeEmpty();
        harness.ThreadedPoster.Replies.Should().ContainSingle()
            .Which.Text.Should().Contain("trigger_id is missing");
    }

    // -----------------------------------------------------------------
    // Orchestrator failures bubble up so the pipeline can retry.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Orchestrator_exception_propagates_so_pipeline_can_retry()
    {
        TestHarness harness = new();
        harness.TaskService.ThrowOnCreate = new InvalidOperationException("orchestrator-down");

        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-flake",
            text: "<@U123> ask plan for failover");

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message == "orchestrator-down");
    }

    // -----------------------------------------------------------------
    // Defensive: envelope with no channel id (malformed payload) is
    // acked without dispatch -- the dedup row claim from the upstream
    // pipeline still suppresses Slack retries.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Missing_channel_id_acks_without_dispatching()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "evt:Ev-noch",
            SourceType: SlackInboundSourceType.Event,
            TeamId: "T1",
            ChannelId: null,
            UserId: "U1",
            RawPayload: """{"type":"event_callback","event_id":"Ev-noch","team_id":"T1","event":{"type":"app_mention","user":"U1","text":"<@U123> ask hello","ts":"1.0"}}""",
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().BeEmpty();
        harness.ThreadedPoster.Replies.Should().BeEmpty();
    }

    [Fact]
    public void Implements_ISlackAppMentionHandler_so_ingestor_pipeline_can_resolve_it()
    {
        typeof(ISlackAppMentionHandler).IsAssignableFrom(typeof(SlackAppMentionHandler))
            .Should().BeTrue();
    }

    // -----------------------------------------------------------------
    // Iter-2 evaluator item 3: when envelope.ChannelId is null but the
    // inner event.channel carries one (a malformed-envelope edge case
    // or a synthetic test envelope), the handler MUST propagate the
    // resolved channel id into the envelope passed to
    // SlackCommandHandler.DispatchAsync so downstream consumers --
    // notably AgentTaskCreationRequest.ChannelId in HandleAskAsync --
    // see the channel needed to anchor the Slack thread.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Ask_with_null_envelope_channel_id_propagates_event_channel_into_create_task_request()
    {
        TestHarness harness = new();

        // Envelope.ChannelId is null, but event.channel == "C-EVT" is
        // carried inside the raw payload. The handler must lift the
        // payload value all the way into AgentTaskCreationRequest.ChannelId.
        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "evt:Ev-chan-fallback",
            SourceType: SlackInboundSourceType.Event,
            TeamId: "T1",
            ChannelId: null,
            UserId: "U1",
            RawPayload: "{\"type\":\"event_callback\",\"event_id\":\"Ev-chan-fallback\","
                + "\"team_id\":\"T1\","
                + "\"event\":{\"type\":\"app_mention\","
                + "\"channel\":\"C-EVT\",\"user\":\"U1\","
                + "\"text\":\"<@U123> ask plan failover\","
                + "\"ts\":\"1700000000.000100\"}}",
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().ContainSingle();
        AgentTaskCreationRequest request = harness.TaskService.CreateRequests[0];
        request.ChannelId.Should().Be(
            "C-EVT",
            "iter-2 evaluator item 3: the resolved fall-back channel_id MUST be propagated into the envelope handed to DispatchAsync so AgentTaskCreationRequest.ChannelId is the channel anchor the thread will live in, not null");

        harness.ThreadedPoster.Replies.Should().ContainSingle();
        harness.ThreadedPoster.Replies[0].ChannelId.Should().Be(
            "C-EVT",
            "the threaded reply MUST also target the resolved fall-back channel");
    }

    // -----------------------------------------------------------------
    // Iter-2 evaluator item 2: the responder is invoked AFTER
    // orchestrator side-effects (CreateTaskAsync /
    // PublishDecisionAsync); a poster exception MUST NOT propagate out
    // of HandleAsync because the inbound pipeline would retry the
    // envelope and re-run the orchestrator call (duplicate task /
    // decision). Caller cancellation is the only exception that still
    // propagates so the ingestor's shutdown loop honours cancellation.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Poster_exception_during_ack_reply_is_swallowed_so_pipeline_does_not_retry_and_duplicate_create_task()
    {
        TestHarness harness = new();
        harness.ThreadedPoster.ThrowOnPost = new InvalidOperationException("slack-postmessage-down");

        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-poster-flake",
            text: "<@U123> ask plan persistence failover");

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "iter-2 evaluator item 2: poster failures MUST be swallowed so the inbound pipeline does NOT retry and re-issue CreateTaskAsync. The orchestrator-side side-effect has already happened by the time the responder fires; turning a missed ack into a retry would duplicate the task.");

        // Orchestrator side-effect happened exactly once.
        harness.TaskService.CreateRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Caller_cancellation_during_poster_call_propagates_so_shutdown_is_honoured()
    {
        TestHarness harness = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        harness.ThreadedPoster.ThrowOnPost = new OperationCanceledException(cts.Token);

        SlackInboundEnvelope envelope = BuildAppMentionEnvelope(
            idempotencyKey: "evt:Ev-poster-cancel",
            text: "<@U123> approve Q-9");

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "OperationCanceledException tied to the caller's token MUST propagate; only background poster failures are swallowed");
    }

    // -----------------------------------------------------------------
    // Harness + fakes
    // -----------------------------------------------------------------
    private sealed class TestHarness
    {
        public TestHarness()
        {
            this.TaskService = new RecordingAgentTaskService();
            this.Responder = new RecordingEphemeralResponder();
            this.ViewsOpenClient = new RecordingViewsOpenClient();
            this.MessageRenderer = new RecordingMessageRenderer();
            this.ThreadedPoster = new RecordingThreadedReplyPoster();

            SlackCommandHandler command = new(
                this.TaskService,
                this.Responder,
                this.ViewsOpenClient,
                this.MessageRenderer,
                new SlackModalAuditRecorder(
                    new InMemorySlackAuditEntryWriter(),
                    NullLogger<SlackModalAuditRecorder>.Instance,
                    TimeProvider.System),
                NullLogger<SlackCommandHandler>.Instance,
                TimeProvider.System);

            this.Handler = new SlackAppMentionHandler(
                command,
                this.ThreadedPoster,
                NullLogger<SlackAppMentionHandler>.Instance);
        }

        public RecordingAgentTaskService TaskService { get; }

        public RecordingEphemeralResponder Responder { get; }

        public RecordingViewsOpenClient ViewsOpenClient { get; }

        public RecordingMessageRenderer MessageRenderer { get; }

        public RecordingThreadedReplyPoster ThreadedPoster { get; }

        public SlackAppMentionHandler Handler { get; }
    }

    private sealed class RecordingAgentTaskService : IAgentTaskService
    {
        private readonly ConcurrentQueue<AgentTaskCreationRequest> createRequests = new();
        private readonly ConcurrentQueue<AgentTaskStatusQuery> statusQueries = new();
        private readonly ConcurrentQueue<HumanDecisionEvent> publishedDecisions = new();

        public IReadOnlyList<AgentTaskCreationRequest> CreateRequests => this.createRequests.ToArray();

        public IReadOnlyList<AgentTaskStatusQuery> StatusQueries => this.statusQueries.ToArray();

        public IReadOnlyList<HumanDecisionEvent> PublishedDecisions => this.publishedDecisions.ToArray();

        public AgentTaskStatusResult NextStatusResult { get; set; } = new(
            Scope: "swarm",
            Summary: string.Empty,
            Entries: Array.Empty<AgentTaskStatusEntry>());

        public Exception? ThrowOnCreate { get; set; }

        public Task<AgentTaskCreationResult> CreateTaskAsync(AgentTaskCreationRequest request, CancellationToken ct)
        {
            this.createRequests.Enqueue(request);
            if (this.ThrowOnCreate is not null)
            {
                throw this.ThrowOnCreate;
            }

            return Task.FromResult(new AgentTaskCreationResult(
                TaskId: "TASK-stub",
                CorrelationId: request.CorrelationId,
                Acknowledgement: "stub-ack"));
        }

        public Task<AgentTaskStatusResult> GetTaskStatusAsync(AgentTaskStatusQuery query, CancellationToken ct)
        {
            this.statusQueries.Enqueue(query);
            return Task.FromResult(this.NextStatusResult);
        }

        public Task PublishDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
        {
            this.publishedDecisions.Enqueue(decision);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEphemeralResponder : ISlackEphemeralResponder
    {
        public List<(string? Url, string Message)> Captured { get; } = new();

        public Task SendEphemeralAsync(string? responseUrl, string message, CancellationToken ct)
        {
            this.Captured.Add((responseUrl, message));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingViewsOpenClient : ISlackViewsOpenClient
    {
        public List<SlackViewsOpenRequest> Requests { get; } = new();

        public SlackViewsOpenResult NextResult { get; set; } = SlackViewsOpenResult.Success();

        public Task<SlackViewsOpenResult> OpenAsync(SlackViewsOpenRequest request, CancellationToken ct)
        {
            this.Requests.Add(request);
            return Task.FromResult(this.NextResult);
        }
    }

    private sealed class RecordingMessageRenderer : ISlackMessageRenderer
    {
        public object RenderReviewModal(SlackReviewModalContext context)
            => new { type = "modal", task_id = context.TaskId };

        public object RenderEscalateModal(SlackEscalateModalContext context)
            => new { type = "modal", task_id = context.TaskId };

        public object RenderCommentModal(SlackCommentModalContext context)
            => new { type = "modal", question_id = context.QuestionId };

        public object RenderQuestion(AgentSwarm.Messaging.Abstractions.AgentQuestion question)
            => new { type = "question", question_id = question.QuestionId };

        public object RenderMessage(AgentSwarm.Messaging.Abstractions.MessengerMessage message)
            => new { type = "message", message_id = message.MessageId };
    }

    private sealed class RecordingThreadedReplyPoster : ISlackThreadedReplyPoster
    {
        private readonly ConcurrentQueue<SlackThreadedReplyRequest> replies = new();

        public IReadOnlyList<SlackThreadedReplyRequest> Replies => this.replies.ToArray();

        /// <summary>
        /// When non-null, the poster throws this exception INSTEAD of
        /// enqueueing the reply. Used to drive iter-2 evaluator
        /// item 2's defensive swallow contract (poster failures MUST
        /// NOT propagate from ThreadedReplyResponder).
        /// </summary>
        public Exception? ThrowOnPost { get; set; }

        public Task PostAsync(SlackThreadedReplyRequest request, CancellationToken ct)
        {
            if (this.ThrowOnPost is not null)
            {
                throw this.ThrowOnPost;
            }

            this.replies.Enqueue(request);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Builds a Slack Events API <c>app_mention</c> envelope with the
    /// supplied text, ts, and optional thread_ts so test scenarios can
    /// pin the exact shape they want to exercise.
    /// </summary>
    private static SlackInboundEnvelope BuildAppMentionEnvelope(
        string idempotencyKey,
        string text,
        string ts = "1700000000.000100",
        string? threadTs = null)
    {
        // Encode the text body Slack would send: a JSON event_callback
        // with an inner app_mention event carrying text + channel +
        // user + ts (+ optional thread_ts).
        string escapedText = System.Text.Json.JsonEncodedText.Encode(text).ToString();
        string threadFragment = threadTs is null
            ? string.Empty
            : $",\"thread_ts\":\"{threadTs}\"";

        string raw = "{\"type\":\"event_callback\",\"event_id\":\"" + idempotencyKey + "\","
            + "\"team_id\":\"T1\","
            + "\"event\":{\"type\":\"app_mention\","
            + "\"channel\":\"C1\","
            + "\"user\":\"U1\","
            + "\"text\":\"" + escapedText + "\","
            + "\"ts\":\"" + ts + "\""
            + threadFragment
            + "}}";

        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Event,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: raw,
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);
    }
}
