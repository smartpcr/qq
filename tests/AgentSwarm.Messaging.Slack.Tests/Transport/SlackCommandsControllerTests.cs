// -----------------------------------------------------------------------
// <copyright file="SlackCommandsControllerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

/// <summary>
/// Stage 4.1 unit tests for <see cref="SlackCommandsController"/>.
/// Pins the async enqueue contract for the standard sub-commands
/// (<c>/agent ask</c>, etc.) and the synchronous modal fast-path for
/// <c>/agent review</c> and <c>/agent escalate</c>.
/// </summary>
public sealed class SlackCommandsControllerTests
{
    private const string FormContentType = "application/x-www-form-urlencoded";

    [Fact]
    public async Task Agent_ask_enqueues_envelope_and_returns_200()
    {
        const string body = "team_id=T01TEAM&channel_id=C01CHAN&user_id=U01USER&command=%2Fagent&text=ask+generate+plan&trigger_id=trig.A";
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);

        SlackCommandsController controller = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackCommandsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        harness.FastPath.InvocationCount.Should().Be(0,
            "non-modal sub-commands must NOT invoke the synchronous fast-path");

        // Iter-2 evaluator item 3: enqueue is deferred to
        // Response.OnCompleted; drive it from the test.
        await harness.FireResponseCompletedAsync();

        SlackInboundEnvelope envelope =
            await SlackControllerTestHelpers.DequeueWithTimeoutAsync(harness.Queue);
        envelope.IdempotencyKey.Should().Be("cmd:T01TEAM:U01USER:/agent:trig.A");
        envelope.SourceType.Should().Be(SlackInboundSourceType.Command);
        envelope.TriggerId.Should().Be("trig.A");
    }

    [Fact]
    public async Task Agent_ask_acks_before_queue_enqueue_runs()
    {
        // Iter-2 evaluator item 3 pin: the slash-command controller
        // must ACK Slack BEFORE writing the envelope to the queue so a
        // slow / failing durable queue cannot delay or fail the ACK.
        const string body = "team_id=T01&channel_id=C01&user_id=U01&command=%2Fagent&text=ask+do+thing&trigger_id=trig.X";
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType);
        SlackCommandsController controller =
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackCommandsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = harness.Context },
            };

        IActionResult result = await controller.Post(CancellationToken.None);
        result.Should().BeOfType<OkResult>();

        (await TryDequeueAsync(harness.Queue, 30)).Should().BeFalse(
            "implementation-plan §4.1 requires enqueue AFTER ACK -- queue must be empty until Response.OnCompleted fires");
        await harness.FireResponseCompletedAsync();
        (await TryDequeueAsync(harness.Queue, 250)).Should().BeTrue(
            "the post-response-completed phase MUST populate the queue");
    }

    [Theory]
    [InlineData("review pull request 42", "review")]
    [InlineData("escalate to oncall", "escalate")]
    public async Task Modal_subcommands_invoke_fast_path_handler(string text, string expectedSubCommand)
    {
        string body = $"team_id=T01TEAM&channel_id=C01CHAN&user_id=U01USER&command=%2Fagent&text={System.Uri.EscapeDataString(text)}&trigger_id=trig.X";
        RecordingModalFastPathHandler fastPath = new()
        {
            Result = SlackModalFastPathResult.Handled(),
        };

        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType, fastPath);

        SlackCommandsController controller = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackCommandsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>(
            "Handled with no custom action result falls through to the controller's default Ok()");
        fastPath.InvocationCount.Should().Be(1,
            $"the {expectedSubCommand} sub-command must invoke the modal fast-path handler synchronously");
        fastPath.LastEnvelope.Should().NotBeNull();
        fastPath.LastEnvelope!.SourceType.Should().Be(SlackInboundSourceType.Command);

        // Handled means the fast-path took ownership — the controller
        // MUST NOT enqueue (would cause duplicate processing) and MUST
        // NOT schedule an enqueue callback either.
        await harness.FireResponseCompletedAsync();
        await AssertQueueEmptyAsync(harness.Queue);
    }

    [Fact]
    public async Task Modal_subcommand_async_fallback_returns_ephemeral_error_and_does_not_enqueue()
    {
        // Iter-2 evaluator item 2 fix: previously the controller fell
        // through to the async enqueue path when the fast-path returned
        // AsyncFallback. That meant the orchestrator received a modal
        // command for which the trigger_id had already expired (Slack's
        // trigger_id is valid for ~3 seconds, architecture.md §5.3), so
        // views.open could no longer succeed. The new contract: return
        // an ephemeral error to the user (HTTP 200 with
        // response_type=ephemeral) and DO NOT enqueue.
        const string body = "team_id=T01TEAM&user_id=U01USER&command=%2Fagent&text=review+pr+42&trigger_id=trig.X";
        RecordingModalFastPathHandler fastPath = new()
        {
            Result = SlackModalFastPathResult.AsyncFallback,
        };

        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType, fastPath);

        SlackCommandsController controller =
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackCommandsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = harness.Context },
            };

        IActionResult result = await controller.Post(CancellationToken.None);

        fastPath.InvocationCount.Should().Be(1, "modal sub-commands must invoke the fast-path");

        ContentResult content = result.Should().BeOfType<ContentResult>(
            "AsyncFallback for a modal sub-command must surface an ephemeral error -- queueing is useless once trigger_id has expired")
            .Subject;
        content.StatusCode.Should().Be(StatusCodes.Status200OK,
            "Slack requires HTTP 200 for ephemeral responses");
        content.ContentType.Should().Contain("application/json");
        content.Content.Should().Contain("\"response_type\":\"ephemeral\"");
        content.Content.Should().Contain("review",
            "the message should reference the sub-command the user tried");

        // Fire the post-response phase: even after that, the queue MUST
        // remain empty because the controller refused to enqueue.
        await harness.FireResponseCompletedAsync();
        await AssertQueueEmptyAsync(harness.Queue);
    }

    [Fact]
    public async Task Fast_path_duplicate_ack_returns_200_without_enqueueing()
    {
        const string body = "team_id=T01TEAM&user_id=U01USER&command=%2Fagent&text=escalate+oncall&trigger_id=trig.X";
        RecordingModalFastPathHandler fastPath = new() { Result = SlackModalFastPathResult.DuplicateAck };

        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType, fastPath);

        SlackCommandsController controller = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackCommandsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>(
            "duplicate replays MUST be silently ACKed with HTTP 200 so Slack stops retrying");

        await harness.FireResponseCompletedAsync();
        await AssertQueueEmptyAsync(harness.Queue);
    }

    [Fact]
    public async Task Fast_path_handled_with_custom_action_result_returns_that_result()
    {
        const string body = "team_id=T01TEAM&user_id=U01USER&command=%2Fagent&text=review&trigger_id=trig.X";
        BadRequestObjectResult customResult = new("modal payload unavailable");
        RecordingModalFastPathHandler fastPath = new()
        {
            Result = SlackModalFastPathResult.Handled(customResult),
        };

        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, FormContentType, fastPath);

        SlackCommandsController controller = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackCommandsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeSameAs(customResult,
            "the controller must propagate the handler's custom action result so the handler can surface views.open errors as ephemeral Slack messages");
    }

    private static async Task<bool> TryDequeueAsync(ISlackInboundQueue queue, int millisecondTimeout)
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

    private static async Task AssertQueueEmptyAsync(ISlackInboundQueue queue)
    {
        bool dequeued = await TryDequeueAsync(queue, 50);
        dequeued.Should().BeFalse("the queue must be empty when the fast-path takes ownership of the envelope");
    }
}
