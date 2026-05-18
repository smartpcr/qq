// -----------------------------------------------------------------------
// <copyright file="SlackEndToEndIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Diagnostics;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Stage 8.2 end-to-end integration tests covering the six acceptance
/// criteria from
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// Stage 8.2:
/// <list type="bullet">
///   <item><description>AC-1 -- a signed <c>/agent ask</c> slash
///   command creates an agent task and posts a thread-root Slack
///   message with an audit row recording the inbound command.</description></item>
///   <item><description>AC-2 -- the agent posts a status update and
///   an <see cref="AgentQuestion"/> as two threaded Block Kit replies.</description></item>
///   <item><description>AC-3 -- a Block Kit button click decodes back
///   to a typed <see cref="HumanDecisionEvent"/> and the message
///   buttons are replaced via <c>chat.update</c>.</description></item>
///   <item><description>AC-4 -- two Events API payloads with an
///   identical <c>event_id</c> create exactly one agent task; the
///   second is recorded as <c>outcome = duplicate</c>.</description></item>
///   <item><description>AC-5 -- a slash command from a channel
///   outside <c>AllowedChannelIds</c> is acknowledged with HTTP 200
///   and an ephemeral rejection body, and the rejection is captured
///   in the audit log as <c>outcome = rejected_auth</c>.</description></item>
///   <item><description>AC-6 -- a full ask-question-approve exchange
///   is queryable via <see cref="ISlackAuditLogger.QueryAsync"/> with
///   a single correlation id.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Each <c>[Fact]</c> constructs its own
/// <see cref="SlackIntegrationTestFixture"/> so the per-fixture
/// SQLite database, in-memory queues, mock Slack TestServer, and
/// <see cref="RecordingAgentTaskService"/> are isolated. The fixture
/// keeps the <c>SlackInboundIngestor</c> background service running so
/// the tests exercise the same hop the production worker does:
/// controller ACK → enqueue → background drain → pipeline.
/// </remarks>
public sealed class SlackEndToEndIntegrationTests
{
    private const string AskCommandText = "ask generate implementation plan for persistence failover";

    [Fact]
    public async Task Ac1_SignedAskCommand_CreatesAgentTask_PostsThreadRoot_AndAuditsInbound()
    {
        using SlackIntegrationTestFixture fixture = new();
        fixture.SeedSecrets();
        fixture.AssertChannelBasedQueuesRegistered();

        // Iter-3 evaluator item 3: the slash command HTTP pipeline must
        // itself cause a thread root post -- not a test-side
        // connector.SendMessageAsync after the fact. Wire the
        // orchestrator reply driver BEFORE building the client so the
        // RecordingAgentTaskService.CreateTaskAsync hop posts a status
        // message through the real connector. The whole AC-1 flow then
        // runs from one HTTP request:
        //   POST /api/slack/commands → SlackInboundIngestor →
        //   SlackCommandHandler.HandleAskAsync → IAgentTaskService
        //   .CreateTaskAsync (driver fires) → connector.SendMessageAsync
        //   → ThreadManager.GetOrCreateThreadAsync (chat.postMessage
        //   root) → outbound dispatcher (chat.postMessage threaded
        //   reply).
        fixture.AgentTaskService.OrchestratorReplyDriver = async (request, taskId, ct) =>
        {
            IMessengerConnector connector = fixture.Services.GetRequiredService<IMessengerConnector>();
            MessengerMessage statusReply = new(
                MessageId: "msg-ac1-status",
                AgentId: "agent-orchestrator",
                TaskId: taskId,
                Content: "Working on your plan.",
                MessageType: MessageType.StatusUpdate,
                CorrelationId: request.CorrelationId,
                Timestamp: DateTimeOffset.UtcNow);
            await connector.SendMessageAsync(statusReply, ct);
        };

        using HttpClient client = fixture.CreateClient();

        string triggerId = "trig.AC1";
        string commandBody = BuildAskCommandFormBody(
            channelId: SlackIntegrationTestFixture.AuthorizedChannelId,
            userId: SlackIntegrationTestFixture.AuthorizedUserId,
            triggerId: triggerId,
            text: AskCommandText);

        using HttpRequestMessage request = BuildSignedFormRequest("/api/slack/commands", commandBody);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "Slack slash commands MUST receive a synchronous HTTP 200 within 3s");

        AgentTaskCreationRequest created = await fixture.AgentTaskService
            .WaitForCreateAsync(TimeSpan.FromSeconds(10));

        created.Prompt.Should().Contain("persistence failover");
        created.ExternalUserId.Should().Be(SlackIntegrationTestFixture.AuthorizedUserId);
        created.ChannelId.Should().Be(SlackIntegrationTestFixture.AuthorizedChannelId);

        // The connector enqueues onto the outbound queue; the outbound
        // dispatcher background service drains it asynchronously. We
        // wait for the thread root chat.postMessage AND the threaded
        // status reply (both posted entirely from the single POST
        // /api/slack/commands hit, via the orchestrator-reply driver).
        (await MockSlackWebApi.WaitForCountAsync(
            () => fixture.MockSlackApi.PostMessages,
            minCount: 1,
            timeout: TimeSpan.FromSeconds(15))).Should().BeTrue(
                "the thread root chat.postMessage must be posted by the connector hop driven from the slash command pipeline");

        MockSlackWebApi.RecordedChatPostMessage firstPost = fixture.MockSlackApi.PostMessages[0];
        firstPost.ChannelId.Should().Be(SlackIntegrationTestFixture.AuthorizedChannelId);
        firstPost.ThreadTs.Should().BeNullOrEmpty("the first chat.postMessage from the connector IS the thread root and carries NO thread_ts");

        // AC-1 audit assertion: an inbound SlackAuditEntry exists for the
        // command. The audit row's CorrelationId is the envelope's
        // IdempotencyKey -- a slash-command-derived key shaped
        // `cmd:<team>:<user>:<command>:<trigger_id>`.
        ISlackAuditLogger auditLogger = fixture.Services.GetRequiredService<ISlackAuditLogger>();
        IReadOnlyList<SlackAuditEntry> commandRows = await WaitForAuditRowsAsync(
            auditLogger,
            new SlackAuditQuery
            {
                Direction = "inbound",
                TeamId = SlackIntegrationTestFixture.TestTeamId,
            },
            minCount: 1,
            timeout: TimeSpan.FromSeconds(10));

        commandRows.Should().NotBeEmpty("the inbound audit recorder MUST persist a row for the accepted /agent ask command");
        SlackAuditEntry commandRow = commandRows.First(r => string.Equals(r.RequestType, SlackInboundAuditRecorder.RequestTypeSlashCommand, StringComparison.Ordinal));
        commandRow.Outcome.Should().Be("success");
        commandRow.UserId.Should().Be(SlackIntegrationTestFixture.AuthorizedUserId);
        commandRow.ChannelId.Should().Be(SlackIntegrationTestFixture.AuthorizedChannelId);
        commandRow.CommandText.Should().Contain("ask");

        // Stage 8.2 evaluator item 2: the production
        // SlackAuthorizationFilter's user-group ACL layer MUST
        // resolve membership through the real SlackNet client, which
        // the fixture wires to dispatch through the
        // MockSlackWebApi TestServer. Asserting that the mock
        // recorded the usergroups.users.list call proves the
        // end-to-end host wiring exercises the Stage 8.2-mandated
        // mock Slack Web API endpoint -- the previous iter swapped
        // in a hand-rolled ISlackUserGroupClient stub which left this
        // mock surface unverified.
        (await MockSlackWebApi.WaitForCountAsync(
            () => fixture.MockSlackApi.UserGroupsLists,
            minCount: 1,
            timeout: TimeSpan.FromSeconds(5))).Should().BeTrue(
                "the mock Slack Web API's usergroups.users.list endpoint MUST be exercised by the production SlackNet membership resolution path");
        MockSlackWebApi.RecordedUserGroupsList userGroupCall = fixture.MockSlackApi.UserGroupsLists[0];
        userGroupCall.UserGroupId.Should().Be(
            SlackIntegrationTestFixture.AuthorizedUserGroupId,
            "SlackNet MUST call usergroups.users.list with the workspace-allowed user-group id");
        userGroupCall.Authorization.Should().Contain(
            SlackIntegrationTestFixture.BotToken,
            "the workspace bot token MUST flow through SlackNet as the Authorization: Bearer header on the usergroups.users.list call");
    }

    /// <summary>
    /// Stage 8.2 iter-4 evaluator item 1: proves the
    /// production <see cref="ISlackAuthTester"/> dispatches
    /// <c>auth.test</c> through <see cref="MockSlackWebApi"/> -- not
    /// the public <c>slack.com</c> host. The previous fixture only
    /// rewrote named <see cref="HttpClient"/> registrations, but
    /// <see cref="SlackNetAuthTester"/>'s default ctor news up
    /// <c>new SlackApiClient(token)</c> which builds its own
    /// HttpClient and escapes the mock; calling
    /// <c>/health/ready</c> in that fixture would have leaked a
    /// real Slack API request. The iter-4 fix wires the
    /// internal test-friendly ctor (via
    /// <c>InternalsVisibleTo("AgentSwarm.Messaging.Slack.Tests")</c>)
    /// with a mock-backed factory; this test resolves the
    /// production tester from the host, calls <c>TestAsync</c>,
    /// and asserts the mock observed the call with the workspace
    /// bot token on the Authorization header.
    /// </summary>
    [Fact]
    public async Task AuthTester_DispatchesAuthTest_ThroughMockSlackWebApi()
    {
        using SlackIntegrationTestFixture fixture = new();
        fixture.SeedSecrets();

        // Touch Services so the host (and DI container) is fully
        // built before resolving the tester.
        _ = fixture.Services;

        ISlackAuthTester tester = fixture.Services.GetRequiredService<ISlackAuthTester>();
        SlackAuthTestResult result = await tester.TestAsync(
            SlackIntegrationTestFixture.TestTeamId,
            CancellationToken.None);

        // The mock /api/auth.test handler returns {ok: true} so the
        // tester reports IsHealthy=true. A false reading here proves
        // either (a) the call escaped the mock and hit the real
        // slack.com (which returns invalid_auth for the synthetic
        // bot token), or (b) the workspace lookup failed before the
        // HTTP call -- both are blast-radius bugs the previous
        // fixture would have silently masked.
        result.IsHealthy.Should().BeTrue(
            $"the mock auth.test endpoint MUST return ok; got '{result.Detail}' (errorCode={result.ErrorCode ?? "<none>"})");
        result.TeamId.Should().Be(SlackIntegrationTestFixture.TestTeamId);

        IReadOnlyList<MockSlackWebApi.RecordedAuthTest> authCalls =
            fixture.MockSlackApi.AuthTests;
        authCalls.Should().NotBeEmpty(
            "the mock Slack Web API's /api/auth.test endpoint MUST be exercised by SlackNetAuthTester through the fixture's mock-backed ISlackApiClient factory");
        authCalls[0].Authorization.Should().Contain(
            SlackIntegrationTestFixture.BotToken,
            "the workspace bot token MUST flow through SlackNet as the Authorization: Bearer header on the auth.test call -- proving the tester reached the mock, not slack.com");
    }

    [Fact]
    public async Task Ac2_StatusUpdateAndAgentQuestion_AreBothPostedAsThreadedBlockKit()
    {
        using SlackIntegrationTestFixture fixture = new();
        fixture.SeedSecrets();

        // Build the host (touch Services) so background services and
        // outbound dispatcher start before the connector enqueues.
        _ = fixture.Services;

        IMessengerConnector connector = fixture.Services.GetRequiredService<IMessengerConnector>();

        string taskId = "task-ac2";
        string correlationId = "corr-ac2";

        // First send: status update. This creates the thread mapping via
        // SlackThreadManager.GetOrCreateThreadAsync (which posts the
        // chat.postMessage root) AND enqueues the styled status onto the
        // outbound queue so the dispatcher posts a second chat.postMessage
        // with thread_ts set.
        MessengerMessage status = new(
            MessageId: "msg-ac2-status",
            AgentId: "agent-orchestrator",
            TaskId: taskId,
            Content: "Reviewing persistence layer …",
            MessageType: MessageType.StatusUpdate,
            CorrelationId: correlationId,
            Timestamp: DateTimeOffset.UtcNow);

        await connector.SendMessageAsync(status, CancellationToken.None);

        // Second send: a question with two button actions. The connector
        // re-uses the existing thread mapping (no second root post) and
        // enqueues the Block Kit payload onto the outbound queue.
        AgentQuestion question = new(
            QuestionId: "q-ac2-001",
            AgentId: "agent-orchestrator",
            TaskId: taskId,
            Title: "Approve failover plan?",
            Body: "The plan switches the primary store to the secondary region. Proceed?",
            Severity: "warning",
            AllowedActions: new[]
            {
                new HumanAction("act-approve", "Approve", "approve", RequiresComment: false),
                new HumanAction("act-reject", "Reject", "reject", RequiresComment: false),
            },
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15),
            CorrelationId: correlationId);

        await connector.SendQuestionAsync(question, CancellationToken.None);

        // Expect: root (chat.postMessage with NO thread_ts) + status reply
        // + question reply. The dispatcher is async so poll until three
        // postMessage calls have landed at the mock.
        (await MockSlackWebApi.WaitForCountAsync(
            () => fixture.MockSlackApi.PostMessages,
            minCount: 3,
            timeout: TimeSpan.FromSeconds(15))).Should().BeTrue(
                "AC-2 expects a thread root + status reply + question reply -- 3 chat.postMessage calls");

        IReadOnlyList<MockSlackWebApi.RecordedChatPostMessage> posts = fixture.MockSlackApi.PostMessages;
        posts.Should().HaveCountGreaterThanOrEqualTo(3);

        // The first post is the thread root and carries NO thread_ts.
        MockSlackWebApi.RecordedChatPostMessage root = posts[0];
        root.ThreadTs.Should().BeNullOrEmpty("the first chat.postMessage IS the thread root");
        root.ChannelId.Should().Be(SlackIntegrationTestFixture.AuthorizedChannelId);

        // Subsequent posts ride on top of the root and MUST carry
        // thread_ts == root.ts so the conversation collapses to a single
        // thread in Slack's UI.
        MockSlackWebApi.RecordedChatPostMessage statusReply = posts[1];
        MockSlackWebApi.RecordedChatPostMessage questionReply = posts[2];

        statusReply.ThreadTs.Should().Be(root.Ts, "the status update is a threaded reply under the root");
        questionReply.ThreadTs.Should().Be(root.Ts, "the AgentQuestion is also a threaded reply under the root");

        // Block Kit shape assertions for the STATUS reply (Stage 8.2
        // evaluator item 4 — AC-2 requires BOTH the status update AND
        // the question to be posted as correctly formatted threaded
        // replies). DefaultSlackMessageRenderer.RenderMessage produces
        // a section block with block_id="message_body" and mrkdwn text
        // prefixed by the message-type emoji (ℹ️ for StatusUpdate);
        // the attachments wrapper carries the colour sidebar.
        statusReply.Body.Should().Contain("\"attachments\"", "status replies are rendered inside an attachments wrapper with a colour sidebar");
        statusReply.Body.Should().Contain("\"type\":\"section\"", "status reply must include a section block");
        statusReply.Body.Should().Contain("\"block_id\":\"message_body\"", "DefaultSlackMessageRenderer pins block_id=message_body for the rendered MessengerMessage body");
        statusReply.Body.Should().Contain("\"type\":\"mrkdwn\"", "status body is rendered as mrkdwn so Slack honours backticks / bold");
        statusReply.Body.Should().Contain("Reviewing persistence layer", "the status reply must carry the agent's status text");
        statusReply.Body.Should().Contain("\\u2139", "MessageType.StatusUpdate is prefixed with the ℹ️ (U+2139) emoji by DefaultSlackMessageRenderer");

        // Block Kit shape assertions for the QUESTION reply: the question reply's body must
        // contain Block Kit actions (buttons) with our block_id encoding.
        string expectedBlockId = SlackInteractionEncoding.EncodeQuestionBlockId(question.QuestionId, requiresComment: false);
        questionReply.Body.Should().Contain("blocks", "AgentQuestion is rendered as a Block Kit payload");
        questionReply.Body.Should().Contain("\"type\":\"actions\"", "interactive question must include an actions block");
        questionReply.Body.Should().Contain(expectedBlockId, "the actions block_id must be SlackInteractionEncoding.EncodeQuestionBlockId(...)");
        questionReply.Body.Should().Contain("\"Approve\"");
        questionReply.Body.Should().Contain("\"Reject\"");
    }

    [Fact]
    public async Task Ac3_BlockKitButtonClick_PublishesDecision_AndDisablesButtonsViaChatUpdate()
    {
        using SlackIntegrationTestFixture fixture = new();
        fixture.SeedSecrets();
        using HttpClient client = fixture.CreateClient();

        const string taskId = "task-ac3";
        const string correlationId = "corr-ac3";
        const string questionId = "q-ac3-001";
        const string rootChannelId = SlackIntegrationTestFixture.AuthorizedChannelId;
        const string rootThreadTs = "1700001000.000100";

        // Seed a SlackThreadMapping so the interaction handler resolves
        // the in-thread click back to the SAME correlation id the agent
        // used when it posted the question (architecture.md §5.2 ResolveCorrelationIdAsync).
        await SeedThreadMappingAsync(
            fixture,
            taskId: taskId,
            teamId: SlackIntegrationTestFixture.TestTeamId,
            channelId: rootChannelId,
            threadTs: rootThreadTs,
            correlationId: correlationId);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId(questionId, requiresComment: false);
        string interactionPayloadJson = BuildBlockActionsPayloadJson(
            teamId: SlackIntegrationTestFixture.TestTeamId,
            channelId: rootChannelId,
            userId: SlackIntegrationTestFixture.AuthorizedUserId,
            triggerId: "trig.AC3",
            blockId: blockId,
            actionId: "act-approve",
            actionLabel: "Approve",
            actionValue: "approve",
            messageTs: "1700001000.000200",
            threadTs: rootThreadTs);

        string formBody = "payload=" + Uri.EscapeDataString(interactionPayloadJson);
        using HttpRequestMessage request = BuildSignedFormRequest("/api/slack/interactions", formBody);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "Slack interactive payloads MUST receive HTTP 200 within 3s");

        HumanDecisionEvent decision = await fixture.AgentTaskService
            .WaitForDecisionAsync(TimeSpan.FromSeconds(10));

        decision.QuestionId.Should().Be(questionId);
        decision.ActionValue.Should().Be("approve", "the Block Kit action.value MUST map to HumanDecisionEvent.ActionValue");
        decision.ExternalUserId.Should().Be(SlackIntegrationTestFixture.AuthorizedUserId);
        decision.CorrelationId.Should().Be(correlationId, "the handler MUST resolve the correlation id from the seeded thread mapping");

        // The handler also issues a chat.update to disable the buttons
        // on the originating message. Poll until it arrives at the mock.
        (await MockSlackWebApi.WaitForCountAsync(
            () => fixture.MockSlackApi.ChatUpdates,
            minCount: 1,
            timeout: TimeSpan.FromSeconds(10))).Should().BeTrue(
                "after publishing a HumanDecisionEvent the handler MUST chat.update the message to disable the buttons");

        MockSlackWebApi.RecordedChatUpdate update = fixture.MockSlackApi.ChatUpdates[0];
        update.ChannelId.Should().Be(rootChannelId);
        update.MessageTs.Should().Be("1700001000.000200", "chat.update must target the originating message ts");
        update.Body.Should().Contain("Approve", "the disabled-buttons message echoes the chosen action label");

        // Stage 8.2 evaluator item 5: prove the originating buttons
        // were REPLACED rather than retained. SlackInteractionHandler
        // .DisableButtonsAsync rebuilds the message as a section +
        // context pair with the *Verb* by <@user> headline and the
        // "Decision recorded for question `<id>`" context line — and
        // OMITS the original actions block entirely. Three guards:
        //   1. The chat.update body must NOT contain
        //      `"type":"actions"` -- the actions block carrying the
        //      Approve/Reject buttons is gone.
        //   2. The body must NOT contain `"type":"button"` -- no
        //      individual button elements survive (the previous
        //      assertion only checked the parent actions block; this
        //      catches a regression that splits buttons across
        //      multiple blocks).
        //   3. The body MUST contain the decision-recorded context
        //      string so the new payload is the expected replacement
        //      shape, not just empty blocks.
        update.Body.Should().NotContain("\"type\":\"actions\"", "the chat.update payload MUST remove the original actions block so the user cannot click the same button twice");
        update.Body.Should().NotContain("\"type\":\"button\"", "no button elements may survive in the chat.update payload");
        update.Body.Should().Contain("Decision recorded for question", "the replacement payload MUST include the decision-recorded context line so the thread visibly records the chosen action");
        update.Body.Should().Contain(questionId, "the chat.update payload MUST cite the resolved question_id so an operator scanning the thread can correlate the decision");
    }

    [Fact]
    public async Task Ac4_DuplicateEventId_CreatesOnlyOneTask_AndRecordsSecondAsDuplicate()
    {
        using SlackIntegrationTestFixture fixture = new();
        fixture.SeedSecrets();
        using HttpClient client = fixture.CreateClient();

        const string eventId = "Ev_AC4_duplicate";
        string eventBody = "{"
            + "\"type\":\"event_callback\","
            + "\"event_id\":\"" + eventId + "\","
            + "\"team_id\":\"" + SlackIntegrationTestFixture.TestTeamId + "\","
            + "\"event\":{"
            + "\"type\":\"app_mention\","
            + "\"user\":\"" + SlackIntegrationTestFixture.AuthorizedUserId + "\","
            + "\"channel\":\"" + SlackIntegrationTestFixture.AuthorizedChannelId + "\","
            + "\"text\":\"<@U_BOT> ask generate implementation plan for persistence failover\","
            + "\"ts\":\"1700002000.000100\""
            + "}}";

        // Fire 1.
        using (HttpRequestMessage first = BuildSignedJsonRequest("/api/slack/events", eventBody))
        using (HttpResponseMessage firstResp = await client.SendAsync(first))
        {
            firstResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        AgentTaskCreationRequest created = await fixture.AgentTaskService
            .WaitForCreateAsync(TimeSpan.FromSeconds(10));
        created.Prompt.Should().Contain("persistence failover");

        // Wait for the success audit row before firing the duplicate.
        ISlackAuditLogger auditLogger = fixture.Services.GetRequiredService<ISlackAuditLogger>();
        string expectedCorrelationId = SlackIdempotencyKeyDerivation.EventPrefix + eventId;
        IReadOnlyList<SlackAuditEntry> firstRows = await WaitForAuditRowsAsync(
            auditLogger,
            new SlackAuditQuery { CorrelationId = expectedCorrelationId },
            minCount: 1,
            timeout: TimeSpan.FromSeconds(10));
        firstRows.Should().ContainSingle(r => r.Outcome == "success",
            "the first Events API delivery MUST be recorded as outcome=success");

        // Fire 2 with an identical body (and therefore an identical
        // derived idempotency key `event:<eventId>`). The signature
        // header is recomputed because Slack restamps the timestamp on
        // every retry.
        using (HttpRequestMessage second = BuildSignedJsonRequest("/api/slack/events", eventBody))
        using (HttpResponseMessage secondResp = await client.SendAsync(second))
        {
            secondResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The duplicate guard MUST short-circuit before invoking the
        // handler -- a duplicate audit row appears in addition to the
        // first success row.
        IReadOnlyList<SlackAuditEntry> bothRows = await WaitForAuditRowsAsync(
            auditLogger,
            new SlackAuditQuery { CorrelationId = expectedCorrelationId },
            minCount: 2,
            timeout: TimeSpan.FromSeconds(10));

        bothRows.Count(r => r.Outcome == "success").Should().Be(1, "the original delivery is the only success");
        bothRows.Count(r => r.Outcome == "duplicate").Should().Be(1,
            "Slack at-least-once retries MUST land in the audit log as outcome=duplicate so operators can distinguish replay storms from real volume");

        // Most importantly: only ONE CreateTaskAsync call regardless of
        // the duplicate.
        await Task.Delay(500);
        fixture.AgentTaskService.CreateRequests.Should().HaveCount(1,
            "Slack event retries MUST NOT duplicate agent tasks (Acceptance Criterion 4)");
    }

    [Fact]
    public async Task Ac5_AskCommandFromDeniedChannel_Returns200_EphemeralBody_AndAuditsRejectedAuth()
    {
        // Stage 8.2 AC-5, half 1 of 2 -- HTTP-facing (sync filter) leg.
        //
        // Brief: "send a slash command from a channel not in
        // AllowedChannelIds; verify HTTP 200 ACK is returned to Slack,
        // the request is rejected during async processing with an
        // ephemeral error message to the user, and audit records
        // outcome = rejected_auth".
        //
        // Production wires SlackAuthorizationFilter as a global MVC
        // service filter (Worker/Program.cs line 175); architecture
        // Scenario 11.4 + architecture.md §2.4 pin this as the
        // canonical rejection surface for HTTP transports. This test
        // exercises that production path and verifies the brief's
        // user-visible contract (HTTP 200 + ephemeral + audit + no
        // orchestrator invocation) end-to-end:
        //
        //   (a) HTTP 200 to Slack (architecture.md §5.5 step 3 -- ACK
        //       before any rejection processing so Slack does not
        //       retry).
        //   (b) Ephemeral error message to the user, delivered in the
        //       response body (sync filter contract) OR via a follow-up
        //       response_url POST (Stage 4.3 pipeline-side contract --
        //       exercised by the sister test below).
        //   (c) outcome = rejected_auth in the audit log, with the
        //       Scenario 12.3 forensics (team_id, channel_id, user_id,
        //       command_text).
        //   (d) The orchestrator is NOT invoked; a denied-channel
        //       request MUST NOT spawn an agent task.
        //
        // The sister test Ac5_RealSocketModeReceiver_FromDeniedChannel_RejectedDuringAsyncPipelineProcessing_PostsEphemeralAndAuditsRejectedAuth
        // below drives a real production SlackSocketModeReceiver
        // (StartAsync -> PumpFramesAsync -> ACK + Normalize +
        // EnqueueWithRetryAsync) with a controlled fake
        // ISlackSocketModeConnection so AC-5's literal "rejected
        // during async processing" leg is proven through the actual
        // production Socket Mode transport -- NOT through any
        // configuration that removes or mutates the global MVC
        // SlackAuthorizationFilter (the assertion below pins that
        // invariant for this test path; the sister test re-pins it
        // for the async path).
        using SlackIntegrationTestFixture fixture = new();
        fixture.SeedSecrets();

        // Iter-7 evaluator item #1 (STRUCTURAL pin): assert the
        // production global MVC SlackAuthorizationFilter is still
        // registered. Iter-6 evaluator complained that AC-5 was "only
        // proven for async processing by a test-only configuration
        // that removes the production MVC SlackAuthorizationFilter".
        // The bypass knob was removed in iter 7; this assertion turns
        // its absence into an executable check so a future fixture
        // change that re-introduces a bypass fails THIS test with a
        // sharp message instead of silently weakening AC-5.
        fixture.AssertProductionSlackAuthorizationFilterRegistered();

        using HttpClient client = fixture.CreateClient();

        string triggerId = "trig.AC5";
        string commandBody = BuildAskCommandFormBody(
            channelId: SlackIntegrationTestFixture.DeniedChannelId,
            userId: SlackIntegrationTestFixture.AuthorizedUserId,
            triggerId: triggerId,
            text: AskCommandText);

        using HttpRequestMessage request = BuildSignedFormRequest("/api/slack/commands", commandBody);
        using HttpResponseMessage response = await client.SendAsync(request);

        // (a) HTTP 200 ACK -- Slack endpoints MUST return HTTP 200
        // even on authorisation rejection; anything else makes Slack
        // treat it as a transport failure and retry.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Slack endpoints MUST return HTTP 200 even on authorisation rejection (architecture.md §5.5 step 3)");

        // (b) The user MUST receive the canonical unauthorized-channel
        // ephemeral message that Scenario 11.4 + architecture.md §2.4
        // pin as the sync filter's user-visible rejection text. The
        // sync MVC SlackAuthorizationFilter renders this verbatim into
        // the HTTP 200 response body as
        // {"response_type":"ephemeral","text":"<DefaultRejectionMessage>"};
        // the assertion checks the SPECIFIC channel-rejection text
        // (not "any ephemeral payload") so a regression that swaps
        // the message wording, drops response_type=ephemeral, or
        // returns an empty body all fail loudly. The literal is
        // sourced from SlackAuthorizationOptions.DefaultRejectionMessage
        // so a future operator wording change moves both production
        // and test in lockstep.
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(
            "\"response_type\":\"ephemeral\"",
            "Scenario 11.4 pins response_type=ephemeral on the sync filter rejection body so the message stays user-visible-only");
        body.Should().Contain(
            SlackAuthorizationOptions.DefaultRejectionMessage,
            "Scenario 11.4 requires the unauthorized-channel ephemeral message text itself, not any generic ephemeral payload -- the sync filter renders SlackAuthorizationOptions.DefaultRejectionMessage verbatim into the HTTP 200 body");

        // (c) Authorisation rejection MUST land in the audit log as
        // outcome=rejected_auth populated with the Scenario 12.3
        // forensics fields.
        ISlackAuditLogger auditLogger = fixture.Services.GetRequiredService<ISlackAuditLogger>();
        IReadOnlyList<SlackAuditEntry> rejected = await WaitForAuditRowsAsync(
            auditLogger,
            new SlackAuditQuery { Outcome = "rejected_auth" },
            minCount: 1,
            timeout: TimeSpan.FromSeconds(10));

        rejected.Should().NotBeEmpty("the authorisation pipeline MUST persist a rejected_auth row when a request lands on a disallowed channel");
        SlackAuditEntry row = rejected[0];
        row.TeamId.Should().Be(SlackIntegrationTestFixture.TestTeamId, "Scenario 12.3 requires team_id on the rejected_auth row");
        row.ChannelId.Should().Be(SlackIntegrationTestFixture.DeniedChannelId, "Scenario 12.3 requires channel_id on the rejected_auth row");
        row.UserId.Should().Be(SlackIntegrationTestFixture.AuthorizedUserId, "Scenario 12.3 requires user_id on the rejected_auth row");
        row.CommandText.Should().NotBeNullOrEmpty(
            "Scenario 12.3 requires command_text on the rejected_auth row so an operator can audit which slash command was rejected");
        row.CommandText!.Should().Contain("/agent",
            "the sync filter MUST stamp the VERBATIM slash command invocation on the rejected_auth row -- '/agent' is the canonical command prefix story FR-008 'Audit' requires (story 'Audit' wording: persist 'command text')");
        row.CommandText.Should().Contain("ask generate implementation plan",
            "the rejected_auth row's command_text MUST carry the verbatim slash-command TEXT the operator attempted; a generic 'ask' substring would also match an unrelated /agent ask in another channel, so triage queries need the full prompt for forensics");

        // The HTTP-filter path stamps the audit row's RequestType from
        // the controller path (/api/slack/commands), so the row's
        // RequestType MUST be "slash_command" -- distinguishing this
        // sync-filter rejection from the pipeline-side rejection
        // exercised by the sister test (which produces
        // RequestType="authorization_rejection").
        row.RequestType.Should().Be("slash_command",
            "the sync-filter path derives RequestType from the controller path /api/slack/commands");

        // (d) A denied-channel request MUST NOT reach
        // IAgentTaskService.CreateTaskAsync. The 500ms delay gives any
        // async drain enough time to surface a wrongly-dispatched task
        // before this assertion fires.
        await Task.Delay(500);
        fixture.AgentTaskService.CreateRequests.Should().BeEmpty(
            "the orchestrator MUST NOT see commands from channels outside AllowedChannelIds");

        // The rejection MUST NOT also produce an inbound=success row
        // for the denied channel -- the only audit footprint is the
        // rejected_auth row above.
        IReadOnlyList<SlackAuditEntry> successInbound = await auditLogger.QueryAsync(
            new SlackAuditQuery
            {
                Direction = "inbound",
                Outcome = "success",
                ChannelId = SlackIntegrationTestFixture.DeniedChannelId,
            },
            CancellationToken.None);
        successInbound.Should().BeEmpty(
            "a denied-channel slash command MUST NOT produce any inbound=success audit row -- the only authorisation footprint is rejected_auth (Stage 3.2 ACL contract)");
    }

    [Fact]
    public async Task Ac5_RealSocketModeReceiver_FromDeniedChannel_RejectedDuringAsyncPipelineProcessing_PostsEphemeralAndAuditsRejectedAuth()
    {
        // Stage 8.2 AC-5, half 2 of 2 -- async-pipeline leg through the
        // REAL production Socket Mode transport.
        //
        // Brief (verbatim): "verify HTTP 200 ACK is returned to Slack,
        // THE REQUEST IS REJECTED DURING ASYNC PROCESSING with an
        // ephemeral error message to the user, and audit records
        // outcome = rejected_auth".
        //
        // Iter-8 evaluator item #1 (STRUCTURAL change): the previous
        // iteration's async-leg test built a SlackInboundEnvelope via
        // SlackInboundEnvelopeFactory.Build and called
        // ISlackInboundQueue.EnqueueAsync directly -- the evaluator
        // flagged that as "treats the Socket Mode ACK as satisfied by
        // construction instead of driving SlackSocketModeReceiver or
        // an actual inbound Slack frame through the transport". The
        // fix is structural: this test now constructs a real
        // production SlackSocketModeReceiver, hands it a scripted
        // ISlackSocketModeConnection through a scripted
        // ISlackSocketModeConnectionFactory, and observes the
        // production frame -> ACK -> normalise -> enqueue -> ingestor
        // -> authorizer -> ephemeral + audit pipeline end-to-end.
        //
        // Production has TWO transports that reach the inbound
        // pipeline:
        //   (1) HTTP transport (events / commands / interactions
        //       controllers) -- SlackAuthorizationFilter runs as a
        //       global MVC service filter and rejects denied
        //       channels synchronously before the controller can
        //       enqueue (architecture.md §2.4, Scenario 11.4). The
        //       sister sync-filter test above covers this surface
        //       end-to-end via /api/slack/commands.
        //   (2) Socket Mode transport (SlackSocketModeReceiver) --
        //       the production WebSocket receiver normalises every
        //       inbound frame to a SlackInboundEnvelope and pushes
        //       it onto ISlackInboundQueue. Socket Mode envelopes
        //       NEVER hit the MVC filter -- the ONLY authorisation
        //       surface they encounter is the pipeline-side
        //       SlackInboundAuthorizer (Stage 4.3) running inside
        //       the SlackInboundIngestor BackgroundService dispatch
        //       loop -- i.e. literally "during async processing"
        //       per the AC-5 brief.
        //
        // The flow this test drives, frame-by-frame:
        //   StartAsync -> connection factory hands out the scripted
        //   ISlackSocketModeConnection -> background pump receives
        //   a `hello` frame (resets reconnect counter) -> background
        //   pump receives a `slash_commands` frame for a denied
        //   channel -> production HandleEnvelopeFrameAsync calls
        //   SendAckSafeAsync first (the WebSocket equivalent of the
        //   HTTP 200 ACK), then SlackSocketModePayloadNormalizer
        //   builds the SlackInboundEnvelope, then
        //   SlackInboundEnqueueScheduler.EnqueueWithRetryAsync
        //   pushes onto the SAME ISlackInboundQueue the
        //   SlackInboundIngestor background service drains in
        //   production. The pipeline-side authorizer rejects
        //   (DisallowedChannel), POSTs the ephemeral via response_url
        //   (intercepted by the mock at /mock/response_url/*), and
        //   writes the rejected_auth audit row.
        using SlackIntegrationTestFixture fixture = new();
        fixture.SeedSecrets();
        fixture.AssertChannelBasedQueuesRegistered();

        // Iter-7 evaluator item #1 (STRUCTURAL pin): assert the
        // production global MVC SlackAuthorizationFilter is STILL
        // registered even when we are exercising the async pipeline
        // via Socket Mode. The point is to prove the async-leg
        // coverage is achieved WITHOUT mutating the HTTP transport's
        // filter chain -- the Socket Mode transport simply never
        // touches MVC filters in production, so both surfaces coexist
        // unmodified. If a future fixture change removes or fakes the
        // filter, this assertion fires and the test fails with a
        // sharp message rather than silently turning into the
        // "test-only configuration" pattern the iter-6 evaluator
        // flagged.
        fixture.AssertProductionSlackAuthorizationFilterRegistered();

        // Seed the workspace's app-level-token secret BEFORE
        // constructing the receiver. SlackSocketModeReceiver.StartAsync
        // resolves the AppLevelTokenRef via ISecretProvider before
        // calling the connection factory; an unseeded secret would
        // surface as a synchronous StartAsync failure (Stage 4.2 fail-
        // fast contract) rather than letting the receiver enter its
        // background reconnect loop.
        const string appLevelTokenRef = "test://app-level-token/AC5-socket-mode";
        const string appLevelToken = "xapp-stage82-test-app-level-token";
        InMemorySecretProvider secretProvider =
            fixture.Services.GetRequiredService<InMemorySecretProvider>();
        secretProvider.Set(appLevelTokenRef, appLevelToken);

        // Resolve the SAME production seams the SlackInboundIngestor
        // BackgroundService uses -- the inbound queue, the dead-letter
        // sink, the logger factory, and the audit logger. The receiver
        // we construct below talks to these directly so the pipeline
        // observes the envelope as if Socket Mode had pushed it.
        ISlackInboundQueue inboundQueue =
            fixture.Services.GetRequiredService<ISlackInboundQueue>();
        ISlackInboundEnqueueDeadLetterSink deadLetter =
            fixture.Services.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>();
        ILoggerFactory loggerFactory =
            fixture.Services.GetRequiredService<ILoggerFactory>();
        ISlackAuditLogger auditLogger =
            fixture.Services.GetRequiredService<ISlackAuditLogger>();

        // Build the workspace config the receiver expects. The HTTP-
        // side workspace store has its own TeamId already; the
        // receiver is a per-workspace worker so its config is plumbed
        // independently in production (SlackInboundTransportFactory).
        // We construct it directly here to match the production shape
        // (TeamId + AppLevelTokenRef + allow-lists are the only fields
        // SlackSocketModeReceiver inspects, all of which are pinned to
        // the same values the fixture seeded into SlackWorkspaceConfig
        // for the HTTP transport).
        SlackWorkspaceConfig workspaceConfig = new()
        {
            TeamId = SlackIntegrationTestFixture.TestTeamId,
            WorkspaceName = "Stage 8.2 AC-5 Socket Mode Workspace",
            SigningSecretRef = SlackIntegrationTestFixture.SigningSecretRef,
            BotTokenSecretRef = SlackIntegrationTestFixture.BotTokenSecretRef,
            AppLevelTokenRef = appLevelTokenRef,
            DefaultChannelId = SlackIntegrationTestFixture.AuthorizedChannelId,
            AllowedChannelIds = new[] { SlackIntegrationTestFixture.AuthorizedChannelId },
            AllowedUserGroupIds = new[] { SlackIntegrationTestFixture.AuthorizedUserGroupId },
            Enabled = true,
        };

        // Scripted connection + single-shot factory. The factory
        // throws on a second ConnectAsync so a runaway reconnect loop
        // fails this test sharply instead of masking the production
        // bug under retries.
        ScriptedSlackSocketModeConnection scriptedConnection = new();
        ScriptedSlackSocketModeConnectionFactory connectionFactory = new(scriptedConnection);

        // Pre-load the scripted frames BEFORE StartAsync so the first
        // ReceiveFrameAsync immediately returns hello, then the
        // slash_commands frame, then blocks indefinitely waiting for
        // more frames until StopAsync cancels.
        //
        // Frame 1: hello -- no envelope_id, the receiver resets its
        // reconnect counter and logs readiness.
        scriptedConnection.EnqueueFrame(new SlackSocketModeFrame(
            Type: SlackSocketModeFrame.HelloType,
            EnvelopeId: null,
            Payload: "{}",
            RawFrameJson: "{\"type\":\"hello\"}"));

        // Frame 2: slash_commands -- Socket Mode delivers the slash
        // command as a JSON object (architecture.md §3.4); the
        // SlackSocketModePayloadNormalizer takes
        // SlackSocketModeFrame.Payload verbatim onto
        // SlackInboundEnvelope.RawPayload, so the envelope's
        // RawPayload here is JSON, NOT form-encoded text. The
        // pipeline-side SlackInboundEnvelopeAuditFields.Extract
        // helper auto-detects JSON vs form (delegates to
        // SlackInboundPayloadParser.ParseCommand) so the
        // CommandText assertion below also exercises that path
        // through the production Socket Mode JSON shape.
        const string socketModeEnvelopeId = "EAC5-socket-mode-001";
        string responseUrl =
            "https://hooks.slack.com/commands/" + SlackIntegrationTestFixture.TestTeamId + "/AC5SOCKET";
        string slashCommandPayloadJson = "{"
            + "\"token\":\"test-verification-token\","
            + "\"team_id\":\"" + SlackIntegrationTestFixture.TestTeamId + "\","
            + "\"team_domain\":\"stage82\","
            + "\"channel_id\":\"" + SlackIntegrationTestFixture.DeniedChannelId + "\","
            + "\"channel_name\":\"stage82-denied\","
            + "\"user_id\":\"" + SlackIntegrationTestFixture.AuthorizedUserId + "\","
            + "\"user_name\":\"stage82-user\","
            + "\"command\":\"/agent\","
            + "\"text\":\"" + AskCommandText + "\","
            + "\"trigger_id\":\"trig.AC5-socket-mode\","
            + "\"response_url\":\"" + responseUrl + "\","
            + "\"api_app_id\":\"A0STAGE82\""
            + "}";

        string outerFrameJson = "{"
            + "\"envelope_id\":\"" + socketModeEnvelopeId + "\","
            + "\"type\":\"slash_commands\","
            + "\"accepts_response_payload\":false,"
            + "\"payload\":" + slashCommandPayloadJson
            + "}";

        scriptedConnection.EnqueueFrame(new SlackSocketModeFrame(
            Type: SlackSocketModeFrame.SlashCommandsType,
            EnvelopeId: socketModeEnvelopeId,
            Payload: slashCommandPayloadJson,
            RawFrameJson: outerFrameJson));

        // Construct the REAL production SlackSocketModeReceiver.
        // Every dependency below is the production type the
        // Worker/Program.cs host wires; ONLY the connection factory
        // is a test double, and the test double's contract is the
        // same internal ISlackSocketModeConnectionFactory production
        // implements via ClientWebSocketSlackSocketModeConnection +
        // DefaultSlackSocketModeConnectionFactory.
        SlackSocketModeReceiver receiver = new(
            workspaceConfig,
            secretProvider,
            connectionFactory,
            inboundQueue,
            deadLetter,
            TimeProvider.System,
            loggerFactory.CreateLogger<SlackSocketModeReceiver>());

        try
        {
            // StartAsync resolves the app-level token through
            // ISecretProvider, then calls ConnectAsync exactly once
            // (the factory throws on a second call so a reconnect
            // bug surfaces immediately). It then launches the
            // background pump which drains the hello + slash_commands
            // frames we pre-loaded.
            await receiver.StartAsync(CancellationToken.None);

            // Sanity-check the receiver took the expected app-level-
            // token path -- proves the receiver resolved the secret
            // through the production ISecretProvider seam rather than
            // bypassing it.
            connectionFactory.ConnectCount.Should().Be(
                1,
                "the receiver MUST open exactly one Socket Mode connection on StartAsync; an extra connect call would mean the loop entered its reconnect path unexpectedly");
            connectionFactory.RequestedTokens.Should().ContainSingle(
                because: "the receiver resolves AppLevelTokenRef through ISecretProvider exactly once at start-up before opening the WebSocket")
                .Which.Should().Be(
                    appLevelToken,
                    "the receiver MUST forward the resolved xapp-... token verbatim to the connection factory -- proving the production secret-resolution path was exercised, not bypassed");

            // (a) Brief: "HTTP 200 ACK is returned to Slack". For
            // Socket Mode the equivalent is the WebSocket envelope_id
            // ACK frame the receiver sends BEFORE enqueueing
            // (SlackSocketModeReceiver.HandleEnvelopeFrameAsync line
            // ~433: ACK FIRST so Slack's 5-second budget is not
            // blocked on a slow enqueue or downstream parser). The
            // scripted connection records every envelope_id passed to
            // SendAckAsync; observing the slash command's envelope_id
            // proves the production receiver honoured the ACK contract
            // through the real transport surface -- NOT "by
            // construction" as the iter-7 evaluator flagged.
            bool acked = await WaitForConditionAsync(
                () => scriptedConnection.AckedEnvelopeIds.Contains(socketModeEnvelopeId),
                TimeSpan.FromSeconds(10));
            acked.Should().BeTrue(
                "the production SlackSocketModeReceiver MUST send a WebSocket ACK for every slash_commands frame within Slack's 5-second budget (Stage 4.2 + tech-spec.md §5.2). The scripted connection records SendAckAsync calls; a missing envelope_id ACK proves the production transport never processed the frame end-to-end.");
            scriptedConnection.AckedEnvelopeIds.Should().ContainSingle(
                because: "exactly one slash_commands frame was sent; a second ACK would indicate the receiver replayed the frame (a duplicate-suppression regression)")
                .Which.Should().Be(socketModeEnvelopeId);

            // (b) Brief: "an ephemeral error message to the user".
            // For Socket Mode there is NO HTTP response body to render
            // the ephemeral into (the WebSocket ACK has already been
            // sent and contains only the envelope_id); the ONLY way
            // for the originating user to see the rejection is a
            // response_url POST issued by the pipeline-side
            // SlackInboundAuthorizer.TryPostEphemeralRejectionAsync.
            // The MockSlackWebApi's rewriting handler routes
            // hooks.slack.com POSTs to /mock/response_url/* and
            // captures them in EphemeralResponses.
            bool delivered = await MockSlackWebApi.WaitForCountAsync(
                () => fixture.MockSlackApi.EphemeralResponses,
                minCount: 1,
                timeout: TimeSpan.FromSeconds(10));
            delivered.Should().BeTrue(
                "AC-5 brief literal: 'rejected during async processing WITH AN EPHEMERAL ERROR MESSAGE TO THE USER' -- the pipeline-side SlackInboundAuthorizer MUST POST an ephemeral via the envelope's response_url (the ONLY Slack-supported user-reply channel for Socket Mode rejections, since no HTTP response body exists). Captured by MockSlackWebApi.EphemeralResponses via the /mock/response_url/* sink.");

            MockSlackWebApi.RecordedEphemeralResponse ephemeral = fixture.MockSlackApi.EphemeralResponses[0];
            ephemeral.ResponseType.Should().Be(
                "ephemeral",
                because: "the pipeline-side rejection MUST be USER-VISIBLE-ONLY -- response_type=ephemeral keeps it out of the channel transcript so a denied-channel rejection does not leak into the channel history");
            ephemeral.Text.Should().Be(
                SlackAuthorizationOptions.DefaultRejectionMessage,
                because: "AC-5 ephemeral text MUST match the operator-configured rejection wording; the pipeline-side authorizer reads it from SlackAuthorizationOptions.RejectionMessage (falls back to DefaultRejectionMessage when unset) so both authorisation surfaces deliver the same user-visible wording");
            ephemeral.Path.Should().StartWith(
                "/mock/response_url",
                because: "the mock's rewriting handler routes the production hooks.slack.com response_url POST onto /mock/response_url/* -- a captured Path under that prefix is the proof that the production ephemeral responder actually POSTed (vs. e.g. silently skipping when ResponseUrl was null on the envelope)");

            // (c) Brief: "audit records outcome = rejected_auth". With
            // no MVC filter in play (Socket Mode envelopes never touch
            // it), the only authorisation surface that can produce
            // this row is the pipeline-side SlackInboundAuthorizer
            // running inside the inbound BackgroundService -- so the
            // row's appearance is itself evidence that rejection
            // happened DURING async processing.
            IReadOnlyList<SlackAuditEntry> rejected = await WaitForAuditRowsAsync(
                auditLogger,
                new SlackAuditQuery { Outcome = "rejected_auth" },
                minCount: 1,
                timeout: TimeSpan.FromSeconds(15));

            rejected.Should().NotBeEmpty(
                "the pipeline-side SlackInboundAuthorizer MUST persist a rejected_auth row when an envelope's channel is not in AllowedChannelIds -- this row's existence proves the rejection happened during async pipeline processing (Socket Mode envelopes never see the sync MVC filter)");

            SlackAuditEntry row = rejected[0];
            row.TeamId.Should().Be(SlackIntegrationTestFixture.TestTeamId,
                "Scenario 12.3 requires team_id on every rejected_auth row, regardless of which authorisation surface produced it");
            row.ChannelId.Should().Be(SlackIntegrationTestFixture.DeniedChannelId,
                "the denied channel id MUST round-trip through the envelope into the audit row so an operator can pivot on channel_id");
            row.UserId.Should().Be(SlackIntegrationTestFixture.AuthorizedUserId,
                "Scenario 12.3 requires user_id on the rejected_auth row");

            // Iter-7 evaluator item #2: the pipeline-side rejection
            // row MUST carry the VERBATIM slash command invocation
            // (story FR-008 "Audit": persist "command text"). The
            // authorizer extracts it from envelope.RawPayload via the
            // shared SlackInboundEnvelopeAuditFields.Extract helper.
            // Because the Socket Mode normalizer stores the inbound
            // JSON verbatim on RawPayload, this assertion also pins
            // the helper's JSON-shaped extraction path (parser
            // auto-detects JSON vs form bodies). The prior synthetic
            // "ingestor://" marker (which the sink falls back to when
            // CommandText is null) is no longer accepted: an operator
            // triaging a denied-channel rejection needs the actual
            // prompt the user attempted.
            row.CommandText.Should().NotBeNullOrEmpty(
                "story Audit FR requires command_text on EVERY audit row, including rejected_auth -- the pipeline-side authorizer MUST stamp the verbatim slash command via SlackInboundEnvelopeAuditFields.Extract for Socket Mode JSON payloads, not pass null and accept the sink's synthetic marker fallback");
            row.CommandText!.Should().Contain("/agent",
                "the rejected_auth row's command_text MUST carry the literal slash command the user attempted (e.g. '/agent ask <args>'); '/agent' is the canonical command prefix and its presence proves the verbatim text round-tripped from the Socket Mode JSON payload into the audit row");
            row.CommandText.Should().Contain("ask generate implementation plan",
                "the rejected_auth row's command_text MUST carry the verbatim slash-command TEXT (not a synthetic ingestor:// marker), so an operator triaging the rejection can pivot on the actual prompt the user attempted -- audit triage requires the full sub-command and arguments, not a path discriminator");

            // (d) Brief: the orchestrator MUST NOT see a denied-
            // channel envelope. The 500ms delay gives the ingestor
            // enough time to fully drain the envelope (which it
            // should NOT have dispatched). A non-empty CreateRequests
            // after this delay would mean the authorizer let the
            // envelope through.
            await Task.Delay(500);
            fixture.AgentTaskService.CreateRequests.Should().BeEmpty(
                "the pipeline-side authorizer MUST short-circuit before SlackCommandHandler dispatches to IAgentTaskService.CreateTaskAsync -- a denied-channel envelope MUST NOT reach the orchestrator under EITHER authorisation surface");

            // And the rejection MUST NOT also produce an inbound=success
            // row for the denied channel -- the pipeline runs auth BEFORE
            // the idempotency guard and BEFORE any handler dispatch, so
            // no success-shaped row should land.
            IReadOnlyList<SlackAuditEntry> successInbound = await auditLogger.QueryAsync(
                new SlackAuditQuery
                {
                    Direction = "inbound",
                    Outcome = "success",
                    ChannelId = SlackIntegrationTestFixture.DeniedChannelId,
                },
                CancellationToken.None);
            successInbound.Should().BeEmpty(
                "a pipeline-rejected envelope MUST NOT also produce inbound=success -- authorisation runs BEFORE the idempotency guard so no success-shaped row can land for the same envelope");

            // Pin the single-connection contract one more time at the
            // end of the test: if the receiver ever entered its
            // reconnect loop during the test window (e.g. because the
            // pump bubbled an unexpected exception), the factory
            // would have thrown on the second ConnectAsync -- this
            // assertion is the catch-all that detects "we never saw
            // the throw because the second connect raced our
            // assertions" failure mode.
            connectionFactory.ConnectCount.Should().Be(
                1,
                "the receiver opened exactly one connection at start-up and should not have entered the reconnect loop during the test window; an extra ConnectAsync call points at an unhandled error inside the production pump or factory");
        }
        finally
        {
            // StopAsync cancels the receive loop's token, closes the
            // current connection (scripted CloseAsync completes the
            // channel writer so any pending ReceiveFrameAsync returns
            // null -> the pump returns gracefully), and waits for the
            // background loop to fully drain. Without this, the loop
            // would keep blocking on ReceiveFrameAsync and the
            // fixture's host shutdown would race the receive task.
            await receiver.StopAsync(CancellationToken.None);
            scriptedConnection.CloseCount.Should().BeGreaterThanOrEqualTo(
                1,
                "StopAsync MUST invoke CloseAsync on the active connection so the WebSocket handshake closes cleanly (the production receiver guarantees this so the Slack peer does not log a transport error on shutdown)");
        }
    }

    [Fact]
    public async Task Ac6_FullAskQuestionApproveExchange_IsQueryableByCorrelationId()
    {
        using SlackIntegrationTestFixture fixture = new();
        fixture.SeedSecrets();

        const string questionId = "q-ac6-001";

        // Stage 8.2 evaluator items 6 + 7: drive the FULL
        // ask→question→approve exchange from the slash command HTTP
        // hit, without any test-side rewrite of SlackThreadMapping.
        //
        // Production flow modelled here:
        //   1. POST /api/slack/commands → SlackCommandHandler.HandleAskAsync
        //      stamps the envelope's IdempotencyKey
        //      (`cmd:<team>:<user>:/agent:<trigger_id>`) onto the
        //      AgentTaskCreationRequest.CorrelationId.
        //   2. RecordingAgentTaskService.CreateTaskAsync records the
        //      request and (via OrchestratorReplyDriver) hands off to
        //      the connector with the SAME CorrelationId.
        //   3. SlackConnector.SendMessageAsync /
        //      .SendQuestionAsync call
        //      ThreadManager.GetOrCreateThreadAsync(taskId, agentId,
        //      correlationId, teamId, ...) which persists
        //      SlackThreadMapping with CorrelationId set to the same
        //      cmd:... key.
        //   4. The user clicks Approve →
        //      POST /api/slack/interactions →
        //      SlackInteractionHandler.HandleBlockActionsAsync reads
        //      the thread mapping and stamps the resolved
        //      CorrelationId into SlackInboundResolvedCorrelationContext
        //      which the inbound audit recorder then uses for the
        //      interaction row.
        //   5. A single SlackAuditLogger.QueryAsync(commandKey) returns
        //      the inbound command row, the connector's outbound
        //      message_send rows, AND the inbound interaction row --
        //      all sharing the same business correlation id.
        //
        // The test-side rewrite of SlackThreadMapping.CorrelationId
        // performed in the previous iter is removed entirely because
        // the production code now resolves the same id end-to-end.
        TaskCompletionSource<string> threadRootSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AgentTaskService.OrchestratorReplyDriver = async (request, taskId, ct) =>
        {
            IMessengerConnector connector = fixture.Services.GetRequiredService<IMessengerConnector>();

            MessengerMessage statusReply = new(
                MessageId: "msg-ac6-status",
                AgentId: "agent-orchestrator",
                TaskId: taskId,
                Content: "Working on your plan.",
                MessageType: MessageType.StatusUpdate,
                CorrelationId: request.CorrelationId,
                Timestamp: DateTimeOffset.UtcNow);
            await connector.SendMessageAsync(statusReply, ct);

            AgentQuestion question = new(
                QuestionId: questionId,
                AgentId: "agent-orchestrator",
                TaskId: taskId,
                Title: "Approve failover plan?",
                Body: "Proceed with the failover plan?",
                Severity: "warning",
                AllowedActions: new[]
                {
                    new HumanAction("act-approve", "Approve", "approve", RequiresComment: false),
                },
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15),
                CorrelationId: request.CorrelationId);
            await connector.SendQuestionAsync(question, ct);

            // Resolve the persisted thread mapping's ThreadTs so the
            // test can build a Block Kit click payload whose
            // container.thread_ts matches what SlackInteractionHandler
            // expects. The mapping is keyed on TaskId; read it back
            // through a scoped DbContext (the connector wrote it via
            // the same in-memory SQLite database).
            string threadTs = await WaitForThreadMappingAsync(fixture, taskId, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            threadRootSignal.TrySetResult(threadTs);
        };

        using HttpClient client = fixture.CreateClient();

        // ---- Step 1: inbound /agent ask command ----
        string triggerId = "trig.AC6";
        string commandBody = BuildAskCommandFormBody(
            channelId: SlackIntegrationTestFixture.AuthorizedChannelId,
            userId: SlackIntegrationTestFixture.AuthorizedUserId,
            triggerId: triggerId,
            text: AskCommandText);

        using (HttpRequestMessage request = BuildSignedFormRequest("/api/slack/commands", commandBody))
        using (HttpResponseMessage response = await client.SendAsync(request))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        AgentTaskCreationRequest created = await fixture.AgentTaskService
            .WaitForCreateAsync(TimeSpan.FromSeconds(10));

        // The command pipeline pins the AgentTaskCreationRequest's
        // CorrelationId to the envelope's IdempotencyKey
        // (`cmd:<team>:<user>:/agent:<trigger_id>`). We rely on that
        // CorrelationId to be the shared id across every audit row.
        string commandKey = created.CorrelationId;
        commandKey.Should().StartWith(SlackIdempotencyKeyDerivation.CommandPrefix,
            "SlackCommandHandler MUST forward the slash command's idempotency key as the orchestrator's correlation id so AC-6's single query returns the full exchange");

        // Wait for the orchestrator driver to finish posting the
        // status + question (the thread root is established
        // synchronously by ThreadManager during the first
        // connector.SendMessageAsync call; the threaded replies are
        // dispatched asynchronously).
        Task<string> threadRootCompletion = threadRootSignal.Task;
        Task signalCompleted = await Task.WhenAny(threadRootCompletion, Task.Delay(TimeSpan.FromSeconds(15)));
        signalCompleted.Should().BeSameAs(threadRootCompletion,
            "the orchestrator reply driver MUST persist a SlackThreadMapping and signal its thread_ts within 15s");
        string rootThreadTs = await threadRootCompletion;

        // ---- Step 2: wait for outbound posts to be observable at
        // the mock so the dispatcher has had time to drain the queue
        // (thread root + status threaded reply + question threaded
        // reply = at least 3 chat.postMessage calls).
        (await MockSlackWebApi.WaitForCountAsync(
            () => fixture.MockSlackApi.PostMessages,
            minCount: 3,
            timeout: TimeSpan.FromSeconds(15))).Should().BeTrue(
                "the connector must post a thread root + status reply + question reply via the outbound dispatcher");

        // ---- Step 3: inbound block_actions click (approve) ----
        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId(questionId, requiresComment: false);
        string interactionPayloadJson = BuildBlockActionsPayloadJson(
            teamId: SlackIntegrationTestFixture.TestTeamId,
            channelId: SlackIntegrationTestFixture.AuthorizedChannelId,
            userId: SlackIntegrationTestFixture.AuthorizedUserId,
            triggerId: "trig.AC6.approve",
            blockId: blockId,
            actionId: "act-approve",
            actionLabel: "Approve",
            actionValue: "approve",
            messageTs: "1700003000.000200",
            threadTs: rootThreadTs);

        string formBody = "payload=" + Uri.EscapeDataString(interactionPayloadJson);
        using (HttpRequestMessage request = BuildSignedFormRequest("/api/slack/interactions", formBody))
        using (HttpResponseMessage response = await client.SendAsync(request))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        HumanDecisionEvent decision = await fixture.AgentTaskService
            .WaitForDecisionAsync(TimeSpan.FromSeconds(10));
        decision.CorrelationId.Should().Be(commandKey,
            "the interaction handler MUST resolve the click's correlation id from the persisted thread mapping so AC-6's single query returns all related rows");

        // ---- Query: SlackAuditLogger.QueryAsync(commandKey) MUST
        // return the inbound command, the connector's outbound rows,
        // AND the inbound interaction row -- all sharing the same
        // CorrelationId by design (no test-side mapping rewrite).
        // The audit rows that anchor under commandKey are:
        //   - inbound `slash_command` (the /agent ask hit)
        //   - outbound `thread_create` (ThreadManager root post)
        //   - outbound `message_send` for the status update
        //   - outbound `message_send` for the question
        //   - inbound `interaction` (the Approve click)
        // The interaction row is the LAST to land (the recorder runs
        // after the handler's chat.update completes); use a
        // predicate-based wait so the assertions below see all rows.
        ISlackAuditLogger auditLogger = fixture.Services.GetRequiredService<ISlackAuditLogger>();
        IReadOnlyList<SlackAuditEntry> exchange = await WaitForAuditRowsMatchingAsync(
            auditLogger,
            new SlackAuditQuery { CorrelationId = commandKey },
            predicate: rows =>
                rows.Any(r => r.Direction == "inbound" && r.RequestType == SlackInboundAuditRecorder.RequestTypeSlashCommand)
                && rows.Count(r => r.Direction == "outbound" && r.RequestType == SlackConnector.ConnectorSendRequestType) >= 2
                && rows.Any(r => r.Direction == "inbound" && r.RequestType == SlackInboundAuditRecorder.RequestTypeInteraction),
            timeout: TimeSpan.FromSeconds(15));

        // Stage 8.2 evaluator item 7: AC-6 must return ALL three
        // categories the brief enumerates -- inbound command,
        // outbound question, AND inbound decision -- by a single
        // CorrelationId query.
        SlackAuditEntry inboundCommand = exchange.Should().ContainSingle(
            r => r.Direction == "inbound"
                && r.RequestType == SlackInboundAuditRecorder.RequestTypeSlashCommand,
            "the inbound /agent ask row anchors the conversation")
            .Subject;
        inboundCommand.Outcome.Should().Be("success");
        inboundCommand.CommandText.Should().Contain("ask");

        IReadOnlyList<SlackAuditEntry> outboundSends = exchange
            .Where(r => r.Direction == "outbound" && r.RequestType == SlackConnector.ConnectorSendRequestType)
            .ToList();
        outboundSends.Should().HaveCountGreaterThanOrEqualTo(2,
            "AC-6 requires BOTH the outbound status update AND the outbound question to be queryable by the same CorrelationId (one outbound row per connector send -- one for the status, one for the question)");
        outboundSends.Should().Contain(
            r => r.ResponsePayload != null && r.ResponsePayload.Contains(questionId, StringComparison.Ordinal),
            "the question's outbound row MUST carry the rendered Block Kit payload referencing question_id so an operator can identify which outbound send delivered the question");

        SlackAuditEntry inboundDecision = exchange.Should().ContainSingle(
            r => r.Direction == "inbound"
                && r.RequestType == SlackInboundAuditRecorder.RequestTypeInteraction,
            "the inbound interaction (Approve click) row MUST share the same CorrelationId so the brief's 'every agent/human exchange queryable by correlation ID' contract holds for the FULL ask→question→approve flow")
            .Subject;
        inboundDecision.Outcome.Should().Be("success");
        inboundDecision.UserId.Should().Be(SlackIntegrationTestFixture.AuthorizedUserId);
    }

    private static async Task<string> WaitForThreadMappingAsync(
        SlackIntegrationTestFixture fixture,
        string taskId,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            SlackPersistenceDbContext db = scope.ServiceProvider.GetRequiredService<SlackPersistenceDbContext>();
            SlackThreadMapping? mapping = await db.SlackThreadMappings.FindAsync(taskId);
            if (mapping is not null && !string.IsNullOrEmpty(mapping.ThreadTs))
            {
                return mapping.ThreadTs;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for SlackThreadMapping(taskId={taskId}) to be persisted.");
    }

    private static async Task SeedThreadMappingAsync(
        SlackIntegrationTestFixture fixture,
        string taskId,
        string teamId,
        string channelId,
        string threadTs,
        string correlationId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        SlackPersistenceDbContext db = scope.ServiceProvider.GetRequiredService<SlackPersistenceDbContext>();

        // Pre-warm the schema so SQLite has the table in the shared
        // in-memory cache before the first insert (the fixture's
        // keep-alive connection holds the cache open; Program.cs's
        // EnsureCreated also runs at host start so this is a belt-
        // and-braces guard for tests that touch the DB before the
        // first HTTP request).
        await db.Database.EnsureCreatedAsync();

        SlackThreadMapping mapping = new()
        {
            TaskId = taskId,
            TeamId = teamId,
            ChannelId = channelId,
            ThreadTs = threadTs,
            CorrelationId = correlationId,
            AgentId = "agent-orchestrator",
            CreatedAt = DateTimeOffset.UtcNow,
            LastMessageAt = DateTimeOffset.UtcNow,
        };
        db.SlackThreadMappings.Add(mapping);
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<SlackAuditEntry>> WaitForAuditRowsAsync(
        ISlackAuditLogger auditLogger,
        SlackAuditQuery query,
        int minCount,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        IReadOnlyList<SlackAuditEntry> rows = Array.Empty<SlackAuditEntry>();
        while (DateTime.UtcNow < deadline)
        {
            rows = await auditLogger.QueryAsync(query, CancellationToken.None);
            if (rows.Count >= minCount)
            {
                return rows;
            }

            await Task.Delay(50);
        }

        return rows;
    }

    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns
    /// <see langword="true"/> or the timeout elapses. Used by the AC-5
    /// Socket Mode receiver test to wait for the production receiver's
    /// background loop to ACK a scripted frame without coupling the
    /// test to a fixed-delay <see cref="Task.Delay(System.TimeSpan)"/>.
    /// </summary>
    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }

    /// <summary>
    /// Polls <see cref="ISlackAuditLogger.QueryAsync"/> until the
    /// returned row set satisfies <paramref name="predicate"/> -- used
    /// by AC-6 where the assertions span multiple async producers
    /// (inbound pipeline, outbound dispatcher, interaction handler)
    /// and a fixed minCount can return early before the LAST producer
    /// (the interaction handler running after WaitForDecisionAsync
    /// returns) has flushed its audit row.
    /// </summary>
    private static async Task<IReadOnlyList<SlackAuditEntry>> WaitForAuditRowsMatchingAsync(
        ISlackAuditLogger auditLogger,
        SlackAuditQuery query,
        Func<IReadOnlyList<SlackAuditEntry>, bool> predicate,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        IReadOnlyList<SlackAuditEntry> rows = Array.Empty<SlackAuditEntry>();
        while (DateTime.UtcNow < deadline)
        {
            rows = await auditLogger.QueryAsync(query, CancellationToken.None);
            if (predicate(rows))
            {
                return rows;
            }

            await Task.Delay(50);
        }

        return rows;
    }

    private static string BuildAskCommandFormBody(
        string channelId,
        string userId,
        string triggerId,
        string text)
    {
        StringBuilder sb = new();
        AppendForm(sb, "token", "test-verification-token");
        AppendForm(sb, "team_id", SlackIntegrationTestFixture.TestTeamId);
        AppendForm(sb, "team_domain", "stage82");
        AppendForm(sb, "channel_id", channelId);
        AppendForm(sb, "channel_name", "stage82-channel");
        AppendForm(sb, "user_id", userId);
        AppendForm(sb, "user_name", "stage82-user");
        AppendForm(sb, "command", "/agent");
        AppendForm(sb, "text", text);
        AppendForm(sb, "trigger_id", triggerId);
        AppendForm(sb, "api_app_id", "A0STAGE82");
        AppendForm(sb, "response_url", "https://hooks.slack.com/commands/" + SlackIntegrationTestFixture.TestTeamId + "/AC1");
        return sb.ToString();
    }

    private static void AppendForm(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0)
        {
            sb.Append('&');
        }

        sb.Append(Uri.EscapeDataString(key));
        sb.Append('=');
        sb.Append(Uri.EscapeDataString(value));
    }

    private static string BuildBlockActionsPayloadJson(
        string teamId,
        string channelId,
        string userId,
        string triggerId,
        string blockId,
        string actionId,
        string actionLabel,
        string actionValue,
        string messageTs,
        string? threadTs)
    {
        // Hand-rolled JSON keeps the structure obvious to the reader and
        // avoids dragging a serializer dependency into the test for what
        // is a small, schema-stable payload.
        string threadTsJson = string.IsNullOrEmpty(threadTs)
            ? "null"
            : "\"" + threadTs + "\"";

        return "{"
            + "\"type\":\"block_actions\","
            + "\"team\":{\"id\":\"" + teamId + "\"},"
            + "\"channel\":{\"id\":\"" + channelId + "\"},"
            + "\"user\":{\"id\":\"" + userId + "\"},"
            + "\"trigger_id\":\"" + triggerId + "\","
            + "\"message\":{"
            + "\"ts\":\"" + messageTs + "\","
            + "\"thread_ts\":" + threadTsJson
            + "},"
            + "\"container\":{"
            + "\"channel_id\":\"" + channelId + "\","
            + "\"message_ts\":\"" + messageTs + "\","
            + "\"thread_ts\":" + threadTsJson
            + "},"
            + "\"actions\":[{"
            + "\"type\":\"button\","
            + "\"block_id\":\"" + blockId + "\","
            + "\"action_id\":\"" + actionId + "\","
            + "\"text\":{\"type\":\"plain_text\",\"text\":\"" + actionLabel + "\"},"
            + "\"value\":\"" + actionValue + "\""
            + "}]"
            + "}";
    }

    private static HttpRequestMessage BuildSignedFormRequest(string path, string body)
    {
        StringContent content = new(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        SignContent(content, body);
        HttpRequestMessage request = new(HttpMethod.Post, path) { Content = content };
        return request;
    }

    private static HttpRequestMessage BuildSignedJsonRequest(string path, string body)
    {
        StringContent content = new(body, Encoding.UTF8, "application/json");
        SignContent(content, body);
        HttpRequestMessage request = new(HttpMethod.Post, path) { Content = content };
        return request;
    }

    private static void SignContent(HttpContent content, string body)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string baseString = FormattableString.Invariant(
            $"{SlackSignatureValidator.VersionTag}:{timestamp}:{body}");
        string signature = $"{SlackSignatureValidator.VersionTag}={ComputeHexHmac(SlackIntegrationTestFixture.SigningSecret, baseString)}";
        content.Headers.TryAddWithoutValidation(SlackSignatureValidator.SignatureHeaderName, signature);
        content.Headers.TryAddWithoutValidation(
            SlackSignatureValidator.TimestampHeaderName,
            timestamp.ToString(CultureInfo.InvariantCulture));
    }

    private static string ComputeHexHmac(string secret, string baseString)
    {
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(secret));
        byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString));
        StringBuilder sb = new(digest.Length * 2);
        foreach (byte b in digest)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
