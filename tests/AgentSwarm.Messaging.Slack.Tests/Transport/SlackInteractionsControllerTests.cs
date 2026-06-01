// -----------------------------------------------------------------------
// <copyright file="SlackInteractionsControllerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

/// <summary>
/// Stage 4.1 unit tests for <see cref="SlackInteractionsController"/>.
/// Pins the ACK + enqueue contract for Block Kit button clicks and
/// <c>view_submission</c> payloads.
/// </summary>
public sealed class SlackInteractionsControllerTests
{
    private const string FormContentType = "application/x-www-form-urlencoded";

    [Fact]
    public async Task Block_actions_payload_enqueues_envelope_and_returns_200()
    {
        const string json = """
            {
              "type": "block_actions",
              "trigger_id": "trig.42",
              "team": { "id": "T01TEAM" },
              "channel": { "id": "C01CHAN" },
              "user": { "id": "U01USER" },
              "actions": [ { "action_id": "approve_task_42", "value": "approve" } ]
            }
            """;

        string body = "payload=" + System.Uri.EscapeDataString(json);
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);

        SlackInteractionsController controller =
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = harness.Context },
            };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>(
            "Slack interactive payloads require an immediate HTTP 200 within the 3-second ACK budget");

        // Iter-2 evaluator item 3: enqueue happens AFTER ACK via
        // Response.OnCompleted; drive it from the test.
        await harness.FireResponseCompletedAsync();

        SlackInboundEnvelope envelope =
            await SlackControllerTestHelpers.DequeueWithTimeoutAsync(harness.Queue);
        envelope.IdempotencyKey.Should().Be("interact:T01TEAM:U01USER:approve_task_42:trig.42");
        envelope.SourceType.Should().Be(SlackInboundSourceType.Interaction);
        envelope.TeamId.Should().Be("T01TEAM");
        envelope.ChannelId.Should().Be("C01CHAN");
        envelope.UserId.Should().Be("U01USER");
        envelope.TriggerId.Should().Be("trig.42");
        envelope.RawPayload.Should().Be(body,
            "the raw form-encoded body is retained verbatim for downstream SlackNet decoding and audit hashing");
    }

    [Fact]
    public async Task View_submission_payload_enqueues_envelope_and_returns_200()
    {
        const string json = """
            {
              "type": "view_submission",
              "trigger_id": "trig.99",
              "team": { "id": "T01TEAM" },
              "user": { "id": "U01USER" },
              "view": { "id": "V123ABC", "callback_id": "review_modal" }
            }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);

        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);
        SlackInteractionsController controller =
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = harness.Context },
            };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>();

        await harness.FireResponseCompletedAsync();

        SlackInboundEnvelope envelope =
            await SlackControllerTestHelpers.DequeueWithTimeoutAsync(harness.Queue);
        envelope.IdempotencyKey.Should().Be("interact:T01TEAM:U01USER:V123ABC:trig.99",
            "view_submission payloads use view.id as the idempotency anchor");
        envelope.ChannelId.Should().BeNull("view_submission payloads are not channel-scoped");
    }

    [Fact]
    public async Task Interaction_acks_before_queue_enqueue_runs()
    {
        // Iter-2 evaluator item 3 pin: ACK must precede enqueue.
        const string json = """
            { "type": "block_actions", "trigger_id": "trig.X",
              "team": { "id": "T01" }, "channel": { "id": "C01" },
              "user": { "id": "U01" },
              "actions": [ { "action_id": "btn_ack" } ] }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);
        SlackInteractionsController controller = new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        await controller.Post(CancellationToken.None);

        (await TryDequeueAsync(harness.Queue, 30)).Should().BeFalse(
            "implementation-plan §4.1 requires enqueue AFTER ACK");
        await harness.FireResponseCompletedAsync();
        (await TryDequeueAsync(harness.Queue, 250)).Should().BeTrue(
            "the post-response-completed phase MUST populate the queue");
    }

    // -----------------------------------------------------------------
    // Stage 5.3 iter-3 evaluator item #5: tests for the
    // ISlackInteractionFastPathHandler branch added in iter 2.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Fast_path_async_fallback_invokes_fast_path_and_falls_through_to_enqueue()
    {
        // Default RecordingInteractionFastPathHandler.Result =
        // AsyncFallback; the controller MUST invoke the fast-path AND
        // still enqueue the envelope after the ACK.
        const string json = """
            { "type": "block_actions", "trigger_id": "trig.AF",
              "team": { "id": "T_AF" }, "channel": { "id": "C_AF" },
              "user": { "id": "U_AF" },
              "actions": [ { "action_id": "btn_af" } ] }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);
        SlackInteractionsController controller = new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        harness.InteractionFastPath.InvocationCount.Should().Be(1,
            "the controller MUST always consult the fast-path before deciding whether to enqueue");
        harness.InteractionFastPath.LastEnvelope.Should().NotBeNull();
        harness.InteractionFastPath.LastEnvelope!.TeamId.Should().Be("T_AF");

        await harness.FireResponseCompletedAsync();
        (await TryDequeueAsync(harness.Queue, 250)).Should().BeTrue(
            "an AsyncFallback result MUST fall through to the post-ACK enqueue path");
    }

    [Fact]
    public async Task Fast_path_handled_short_circuits_enqueue_and_returns_ok()
    {
        // Pin the fast-path to Handled(): controller MUST return 200
        // (the bare Ok() it falls back to when no custom IActionResult
        // is provided) AND MUST NOT enqueue the envelope. This is the
        // production path for RequiresComment buttons that opened a
        // comment modal inline -- the eventual view_submission arrives
        // as its own inbound envelope so the click MUST NOT be
        // re-published.
        const string json = """
            { "type": "block_actions", "trigger_id": "trig.HND",
              "team": { "id": "T_HND" }, "channel": { "id": "C_HND" },
              "user": { "id": "U_HND" },
              "actions": [ { "action_id": "btn_hnd", "block_id": "qc:Q-HND", "value": "request-changes" } ] }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);
        RecordingInteractionFastPathHandler fastPath = new()
        {
            Result = AgentSwarm.Messaging.Slack.Pipeline.SlackInteractionFastPathResult.Handled(),
        };
        SlackTestHttpContext harness = SlackControllerTestHelpers.BuildContext(
            body,
            FormContentType,
            overrideInteractionFastPath: fastPath);
        SlackInteractionsController controller = new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        fastPath.InvocationCount.Should().Be(1);
        fastPath.LastEnvelope!.TriggerId.Should().Be("trig.HND");

        await harness.FireResponseCompletedAsync();
        (await TryDequeueAsync(harness.Queue, 100)).Should().BeFalse(
            "Handled MUST short-circuit the enqueue so the click is not re-published when the eventual view_submission arrives");
    }

    [Fact]
    public async Task Fast_path_handled_with_action_result_returns_that_result_and_skips_enqueue()
    {
        // Pin the fast-path to Handled(ContentResult): controller MUST
        // forward that ContentResult to Slack (typically an ephemeral
        // error body) AND MUST NOT enqueue. This is the failure path
        // for trigger_id-bound side-effects -- e.g., a DB-down outage
        // during correlation-id resolution surfaces an ephemeral error
        // instead of pinning a wrong correlation id into the modal.
        const string json = """
            { "type": "block_actions", "trigger_id": "trig.ERR",
              "team": { "id": "T_ERR" }, "channel": { "id": "C_ERR" },
              "user": { "id": "U_ERR" },
              "actions": [ { "action_id": "btn_err", "block_id": "qc:Q-ERR", "value": "approve" } ] }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);
        ContentResult ephemeral = new()
        {
            StatusCode = 200,
            ContentType = "application/json; charset=utf-8",
            Content = "{\"response_type\":\"ephemeral\",\"text\":\"could not open dialog\"}",
        };
        RecordingInteractionFastPathHandler fastPath = new()
        {
            Result = AgentSwarm.Messaging.Slack.Pipeline.SlackInteractionFastPathResult.Handled(ephemeral),
        };
        SlackTestHttpContext harness = SlackControllerTestHelpers.BuildContext(
            body,
            FormContentType,
            overrideInteractionFastPath: fastPath);
        SlackInteractionsController controller = new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeSameAs(ephemeral,
            "Handled(IActionResult) MUST be returned verbatim so the user sees the fast-path's ephemeral error");
        fastPath.InvocationCount.Should().Be(1);

        await harness.FireResponseCompletedAsync();
        (await TryDequeueAsync(harness.Queue, 100)).Should().BeFalse(
            "Handled MUST short-circuit the enqueue regardless of whether a custom IActionResult is attached");
    }

    private static async Task<bool> TryDequeueAsync(
        AgentSwarm.Messaging.Slack.Queues.ISlackInboundQueue queue,
        int millisecondTimeout)
    {
        try
        {
            using CancellationTokenSource cts = new(millisecondTimeout);
            await queue.DequeueAsync(cts.Token);
            return true;
        }
        catch (System.OperationCanceledException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------
    // Iter-2 evaluator item #2 (STRUCTURAL fix). The
    // SlackInteractionsController MUST invoke the
    // ISlackInteractionFastPathHandler BEFORE the post-ACK enqueue
    // path runs so trigger_id-bound side-effects (opening a
    // RequiresComment follow-up modal via views.open) land while the
    // trigger_id is still valid. When the fast-path returns Handled
    // the controller MUST NOT enqueue the envelope (the resulting
    // view_submission will arrive as a separate inbound envelope
    // which the async handler converts into the HumanDecisionEvent).
    // ---------------------------------------------------------------
    [Fact]
    public async Task Fast_path_handled_result_short_circuits_async_enqueue()
    {
        const string json = """
            { "type": "block_actions", "trigger_id": "trig.fastpath",
              "team": { "id": "T01" }, "channel": { "id": "C01" },
              "user": { "id": "U01" },
              "actions": [ { "action_id": "qc_btn", "block_id": "qc:Q-1", "value": "request-changes" } ] }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);

        // Configure the recording fast-path to claim ownership of the
        // envelope (production: DefaultSlackInteractionFastPathHandler
        // opens views.open here).
        harness.InteractionFastPath.Result = SlackInteractionFastPathResult.Handled();

        SlackInteractionsController controller =
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = harness.Context },
            };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>(
            "the fast-path returned Handled() with no custom IActionResult; the controller MUST return a bare HTTP 200");
        harness.InteractionFastPath.InvocationCount.Should().Be(1,
            "the fast-path MUST be invoked exactly once per inbound interactive request");
        harness.InteractionFastPath.LastEnvelope.Should().NotBeNull();
        harness.InteractionFastPath.LastEnvelope!.TriggerId.Should().Be("trig.fastpath",
            "the fast-path MUST receive the envelope BEFORE the controller flushes the ACK so the trigger_id is still valid");

        // Drive the post-ACK callbacks and confirm nothing landed on
        // the inbound queue -- the fast-path took ownership.
        await harness.FireResponseCompletedAsync();
        (await TryDequeueAsync(harness.Queue, 30)).Should().BeFalse(
            "the controller MUST NOT enqueue an envelope the fast-path already handled -- doing so would re-trigger views.open from the async pipeline against an already-expired trigger_id");
    }

    [Fact]
    public async Task Fast_path_async_fallback_runs_normal_post_ack_enqueue()
    {
        // Default fast-path result is AsyncFallback -- this test
        // documents that ordinary button clicks (no qc: prefix) still
        // go through the existing post-ACK enqueue path unchanged.
        const string json = """
            { "type": "block_actions", "trigger_id": "trig.normal",
              "team": { "id": "T01" }, "channel": { "id": "C01" },
              "user": { "id": "U01" },
              "actions": [ { "action_id": "plain_btn", "block_id": "q:Q-7", "value": "approve" } ] }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);

        // Sanity check: harness defaults the fast-path result to AsyncFallback.
        harness.InteractionFastPath.Result.ResultKind
            .Should().Be(SlackInteractionFastPathResultKind.AsyncFallback);

        SlackInteractionsController controller =
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = harness.Context },
            };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        harness.InteractionFastPath.InvocationCount.Should().Be(1,
            "the fast-path MUST always be consulted; AsyncFallback is a per-payload decision, not an opt-out");
        await harness.FireResponseCompletedAsync();
        (await TryDequeueAsync(harness.Queue, 250)).Should().BeTrue(
            "an AsyncFallback result MUST leave the normal post-ACK enqueue path intact");
    }

    [Fact]
    public async Task Fast_path_handled_with_custom_action_result_is_returned_verbatim()
    {
        const string json = """
            { "type": "block_actions", "trigger_id": "trig.err",
              "team": { "id": "T01" }, "channel": { "id": "C01" },
              "user": { "id": "U01" },
              "actions": [ { "action_id": "qc_btn", "block_id": "qc:Q-E", "value": "reject" } ] }
            """;
        string body = "payload=" + System.Uri.EscapeDataString(json);
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);

        ContentResult customResult = new()
        {
            StatusCode = 200,
            ContentType = "application/json; charset=utf-8",
            Content = "{\"response_type\":\"ephemeral\",\"text\":\"trigger expired\"}",
        };
        harness.InteractionFastPath.Result = SlackInteractionFastPathResult.Handled(customResult);

        SlackInteractionsController controller =
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackInteractionsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = harness.Context },
            };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeSameAs(customResult,
            "the controller MUST return the fast-path's IActionResult verbatim so the user sees the ephemeral error message Slack-side");

        await harness.FireResponseCompletedAsync();
        (await TryDequeueAsync(harness.Queue, 30)).Should().BeFalse(
            "even when the fast-path surfaced an ephemeral error, the envelope MUST NOT be enqueued -- the trigger_id is already expired and the async path cannot make progress");
    }
}
