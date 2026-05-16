// -----------------------------------------------------------------------
// <copyright file="SlackInteractionsControllerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System.Threading;
using System.Threading.Tasks;
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
}
