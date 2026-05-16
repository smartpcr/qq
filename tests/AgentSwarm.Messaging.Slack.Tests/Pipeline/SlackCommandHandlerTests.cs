// -----------------------------------------------------------------------
// <copyright file="SlackCommandHandlerTests.cs" company="Microsoft Corp.">
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
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 5.1 brief-mandated tests for
/// <see cref="SlackCommandHandler"/>. The three test scenarios in
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (Stage 5.1) plus regression coverage for the supporting handlers
/// (status, reject, modal sub-commands, missing arguments).
/// </summary>
public sealed class SlackCommandHandlerTests
{
    // -----------------------------------------------------------------
    // Scenario 1: Ask command creates task -- IAgentTaskService.CreateTaskAsync
    // called with the prompt text.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Ask_command_invokes_CreateTaskAsync_with_prompt_text()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-ask",
            text: "ask generate implementation plan for persistence failover");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().ContainSingle();
        AgentTaskCreationRequest request = harness.TaskService.CreateRequests[0];
        request.Prompt.Should().Be("generate implementation plan for persistence failover");
        request.Messenger.Should().Be(SlackCommandHandler.MessengerName);
        request.ExternalUserId.Should().Be("U1");
        request.ChannelId.Should().Be("C1");
        request.CorrelationId.Should().Be(envelope.IdempotencyKey,
            "the dispatcher MUST propagate the envelope's idempotency key as the correlation id so audit / thread mapping align");

        // Nothing else should have been invoked.
        harness.TaskService.StatusQueries.Should().BeEmpty();
        harness.TaskService.PublishedDecisions.Should().BeEmpty();
        harness.ViewsOpenClient.Requests.Should().BeEmpty();

        // The user should receive an ephemeral acknowledgement
        // (post-ACK response_url reply).
        harness.Responder.Messages.Should().ContainSingle();
        harness.Responder.Messages[0].Url.Should().Be("https://hooks.slack.com/resp/abc");
    }

    // -----------------------------------------------------------------
    // Scenario 2: Approve command publishes HumanDecisionEvent with
    // ActionValue = "approve" and QuestionId = "Q-123".
    // -----------------------------------------------------------------
    [Fact]
    public async Task Approve_command_publishes_HumanDecisionEvent_with_question_id_and_approve_action()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-approve",
            text: "approve Q-123");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        HumanDecisionEvent decision = harness.TaskService.PublishedDecisions[0];
        decision.QuestionId.Should().Be("Q-123");
        decision.ActionValue.Should().Be("approve");
        decision.Messenger.Should().Be(SlackCommandHandler.MessengerName);
        decision.ExternalUserId.Should().Be("U1");
        decision.CorrelationId.Should().Be(envelope.IdempotencyKey);
        decision.Comment.Should().BeNull("the approve sub-command carries no free-text comment");

        harness.TaskService.CreateRequests.Should().BeEmpty();
        harness.TaskService.StatusQueries.Should().BeEmpty();
        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("approve").And.Contain("Q-123");
    }

    [Fact]
    public async Task Reject_command_publishes_HumanDecisionEvent_with_reject_action()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-reject",
            text: "reject Q-77");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().ContainSingle();
        HumanDecisionEvent decision = harness.TaskService.PublishedDecisions[0];
        decision.QuestionId.Should().Be("Q-77");
        decision.ActionValue.Should().Be("reject");
    }

    // -----------------------------------------------------------------
    // Scenario 3: Unknown sub-command returns an ephemeral error
    // listing valid sub-commands.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Unknown_sub_command_replies_ephemeral_error_listing_valid_sub_commands()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-unknown",
            text: "unknown some args");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        // No orchestrator side-effects.
        harness.TaskService.CreateRequests.Should().BeEmpty();
        harness.TaskService.PublishedDecisions.Should().BeEmpty();
        harness.TaskService.StatusQueries.Should().BeEmpty();
        harness.ViewsOpenClient.Requests.Should().BeEmpty();

        // Single ephemeral reply mentioning every valid sub-command.
        harness.Responder.Messages.Should().ContainSingle();
        string body = harness.Responder.Messages[0].Message;
        body.Should().Contain("unknown",
            "the error message MUST quote the offending sub-command so the user sees what they typed");
        body.Should().Contain("ask")
            .And.Contain("status")
            .And.Contain("approve")
            .And.Contain("reject")
            .And.Contain("review")
            .And.Contain("escalate");
    }

    [Fact]
    public async Task Empty_text_replies_ephemeral_usage_and_does_not_invoke_orchestrator()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-empty",
            text: string.Empty);

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().BeEmpty();
        harness.TaskService.PublishedDecisions.Should().BeEmpty();
        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("Missing sub-command")
            .And.Contain("ask")
            .And.Contain("approve");
    }

    [Fact]
    public async Task Ask_with_missing_prompt_replies_usage_error_and_does_not_invoke_orchestrator()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-ask-bare",
            text: "ask   ");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.CreateRequests.Should().BeEmpty();
        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("`/agent ask` requires a prompt");
    }

    [Fact]
    public async Task Approve_with_missing_question_id_replies_usage_error_and_does_not_publish()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-approve-bare",
            text: "approve");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().BeEmpty();
        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("`/agent approve` requires a question-id");
    }

    [Fact]
    public async Task Reject_with_missing_question_id_replies_usage_error_and_does_not_publish()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-reject-bare",
            text: "reject");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.PublishedDecisions.Should().BeEmpty();
        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("`/agent reject` requires a question-id");
    }

    [Fact]
    public async Task Status_with_task_id_queries_orchestrator_for_single_task_scope()
    {
        TestHarness harness = new();
        harness.TaskService.NextStatusResult = new AgentTaskStatusResult(
            Scope: "task",
            Summary: "Task TASK-42 is running.",
            Entries: new[] { new AgentTaskStatusEntry("TASK-42", "running", "writing plan") });

        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-status",
            text: "status TASK-42");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.StatusQueries.Should().ContainSingle();
        AgentTaskStatusQuery query = harness.TaskService.StatusQueries[0];
        query.TaskId.Should().Be("TASK-42");
        query.Messenger.Should().Be(SlackCommandHandler.MessengerName);
        query.CorrelationId.Should().Be(envelope.IdempotencyKey);

        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("TASK-42").And.Contain("running");
    }

    [Fact]
    public async Task Status_with_no_task_id_queries_orchestrator_for_swarm_scope()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-status-all",
            text: "status");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.TaskService.StatusQueries.Should().ContainSingle();
        harness.TaskService.StatusQueries[0].TaskId.Should().BeNull(
            "status without an argument queries the entire swarm");
    }

    // -----------------------------------------------------------------
    // Review / escalate (modal fast-path sub-commands). In the async
    // pipeline path (where SlackCommandHandler runs) the handler MUST
    // still attempt views.open so brief steps 6 and 7 are satisfied;
    // a missing trigger_id or a Slack failure surfaces as an
    // ephemeral hint.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Review_command_calls_views_open_with_renderer_built_payload_carrying_task_id()
    {
        TestHarness harness = new();
        harness.ViewsOpenClient.NextResult = SlackViewsOpenResult.Success();

        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-review",
            text: "review TASK-42",
            triggerId: "trig-review");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ViewsOpenClient.Requests.Should().ContainSingle();
        SlackViewsOpenRequest request = harness.ViewsOpenClient.Requests[0];
        request.TeamId.Should().Be("T1");
        request.TriggerId.Should().Be("trig-review");
        request.ViewPayload.Should().NotBeNull();

        // Iter-2 evaluator item 2 fix: assert the renderer received
        // the task-id parsed from the command arguments (not just a
        // sub-command marker), and that the envelope-derived
        // routing context was threaded through.
        harness.MessageRenderer.LastReviewContext.Should().NotBeNull();
        SlackReviewModalContext reviewCtx = harness.MessageRenderer.LastReviewContext!.Value;
        reviewCtx.TaskId.Should().Be("TASK-42");
        reviewCtx.TeamId.Should().Be("T1");
        reviewCtx.ChannelId.Should().Be("C1");
        reviewCtx.UserId.Should().Be("U1");
        reviewCtx.CorrelationId.Should().Be(envelope.IdempotencyKey);
        harness.MessageRenderer.LastEscalateContext.Should().BeNull();

        harness.Responder.Messages.Should().BeEmpty(
            "a successful modal open does not need an ephemeral consolation message");
    }

    [Fact]
    public async Task Escalate_command_calls_views_open_with_renderer_built_payload_carrying_task_id()
    {
        TestHarness harness = new();
        harness.ViewsOpenClient.NextResult = SlackViewsOpenResult.Success();

        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-escalate",
            text: "escalate TASK-9",
            triggerId: "trig-escalate");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ViewsOpenClient.Requests.Should().ContainSingle();
        harness.MessageRenderer.LastEscalateContext.Should().NotBeNull();
        SlackEscalateModalContext escalateCtx = harness.MessageRenderer.LastEscalateContext!.Value;
        escalateCtx.TaskId.Should().Be("TASK-9");
        escalateCtx.TeamId.Should().Be("T1");
        harness.MessageRenderer.LastReviewContext.Should().BeNull();
    }

    [Fact]
    public async Task Review_without_trigger_id_replies_ephemeral_and_skips_views_open()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-review-expired",
            text: "review TASK-42",
            triggerId: null);

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ViewsOpenClient.Requests.Should().BeEmpty();
        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("trigger_id is missing");
    }

    [Fact]
    public async Task Review_with_missing_task_id_replies_usage_error_and_skips_views_open()
    {
        TestHarness harness = new();
        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-review-bare",
            text: "review",
            triggerId: "trig-x");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.ViewsOpenClient.Requests.Should().BeEmpty();
        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("`/agent review` requires a task-id");
    }

    [Fact]
    public async Task Review_views_open_failure_surfaces_ephemeral_error_with_slack_error_code()
    {
        TestHarness harness = new();
        harness.ViewsOpenClient.NextResult = SlackViewsOpenResult.Failure("expired_trigger_id");

        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-review-fail",
            text: "review TASK-42",
            triggerId: "trig-x");

        await harness.Handler.HandleAsync(envelope, CancellationToken.None);

        harness.Responder.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("expired_trigger_id");
    }

    [Fact]
    public async Task Orchestrator_exception_propagates_so_pipeline_can_retry()
    {
        TestHarness harness = new();
        harness.TaskService.ThrowOnCreate = new InvalidOperationException("orchestrator-down");

        SlackInboundEnvelope envelope = BuildCommandEnvelope(
            idempotencyKey: "cmd:T1:U1:/agent:trig-ask-flake",
            text: "ask plan for failover");

        Func<Task> act = () => harness.Handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message == "orchestrator-down");
    }

    [Fact]
    public void Implements_ISlackCommandHandler_so_ingestor_pipeline_can_resolve_it()
    {
        typeof(ISlackCommandHandler).IsAssignableFrom(typeof(SlackCommandHandler))
            .Should().BeTrue();
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
            this.Handler = new SlackCommandHandler(
                this.TaskService,
                this.Responder,
                this.ViewsOpenClient,
                this.MessageRenderer,
                NullLogger<SlackCommandHandler>.Instance,
                TimeProvider.System);
        }

        public RecordingAgentTaskService TaskService { get; }

        public RecordingEphemeralResponder Responder { get; }

        public RecordingViewsOpenClient ViewsOpenClient { get; }

        public RecordingMessageRenderer MessageRenderer { get; }

        public SlackCommandHandler Handler { get; }
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
        private readonly ConcurrentQueue<EphemeralCall> calls = new();

        public IReadOnlyList<EphemeralCall> Messages => this.calls.ToArray();

        public Task SendEphemeralAsync(string? responseUrl, string message, CancellationToken ct)
        {
            this.calls.Enqueue(new EphemeralCall(responseUrl ?? string.Empty, message));
            return Task.CompletedTask;
        }
    }

    internal sealed record EphemeralCall(string Url, string Message);

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
            return new { type = "modal", callback_id = "agent_review_modal", task_id = context.TaskId };
        }

        public object RenderEscalateModal(SlackEscalateModalContext context)
        {
            this.LastEscalateContext = context;
            return new { type = "modal", callback_id = "agent_escalate_modal", task_id = context.TaskId };
        }

        public object RenderCommentModal(SlackCommentModalContext context)
        {
            this.LastCommentContext = context;
            return new { type = "modal", callback_id = SlackInteractionEncoding.CommentCallbackId, question_id = context.QuestionId };
        }
    }

    private static SlackInboundEnvelope BuildCommandEnvelope(
        string idempotencyKey,
        string text,
        string? triggerId = "trig-default")
    {
        // Encode the raw payload as Slack would: application/x-www-form-urlencoded.
        // text and trigger_id are URL-encoded so the parser exercises the
        // same code path it uses for real requests.
        string encodedText = Uri.EscapeDataString(text);
        string body = $"token=xoxb&team_id=T1&channel_id=C1&user_id=U1&command=%2Fagent&text={encodedText}&response_url=https%3A%2F%2Fhooks.slack.com%2Fresp%2Fabc";
        if (!string.IsNullOrEmpty(triggerId))
        {
            body += "&trigger_id=" + Uri.EscapeDataString(triggerId);
        }

        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: body,
            TriggerId: triggerId,
            ReceivedAt: DateTimeOffset.UtcNow);
    }
}
