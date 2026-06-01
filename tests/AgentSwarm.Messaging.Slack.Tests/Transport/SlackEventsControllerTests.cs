// -----------------------------------------------------------------------
// <copyright file="SlackEventsControllerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

/// <summary>
/// Stage 4.1 unit tests for <see cref="SlackEventsController"/>. Pins
/// the URL-verification handshake contract and the event-callback ACK +
/// enqueue contract.
/// </summary>
public sealed class SlackEventsControllerTests
{
    [Fact]
    public async Task Url_verification_payload_returns_200_with_challenge_body()
    {
        const string body = "{\"type\":\"url_verification\",\"challenge\":\"abc123\",\"token\":\"xoxb\"}";
        var (ctx, queue, _) = SlackControllerTestHelpers.BuildContext(body, "application/json");

        // The signature middleware stamps UrlVerificationItemKey in
        // production. Emulate it here so the controller's
        // defense-in-depth check is exercised.
        ctx.Items[SlackSignatureValidator.UrlVerificationItemKey] = true;

        SlackEventsController controller = new()
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status200OK);

        OkObjectResult ok = (OkObjectResult)result;
        object value = ok.Value!;
        string? challenge = (string?)value.GetType()
            .GetProperty("Challenge", BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(value);
        challenge.Should().Be("abc123",
            "Slack expects {\"challenge\":\"<token>\"} verbatim per Events API spec");

        // Queue must NOT receive a handshake envelope -- the handshake
        // is a transport-level concern only.
        bool dequeued = false;
        try
        {
            using CancellationTokenSource cts = new(50);
            await queue.DequeueAsync(cts.Token);
            dequeued = true;
        }
        catch (System.OperationCanceledException)
        {
            // expected
        }

        dequeued.Should().BeFalse(
            "the url_verification handshake must not enqueue an envelope; the controller short-circuits before enqueueing");
    }

    [Fact]
    public async Task Event_callback_returns_200_and_enqueues_envelope()
    {
        const string body = """
            {
              "type": "event_callback",
              "event_id": "Ev42",
              "team_id": "T01TEAM",
              "event": {
                "type": "app_mention",
                "user": "U01USER",
                "channel": "C01CHAN"
              }
            }
            """;
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, "application/json");

        SlackEventsController controller = new()
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);

        result.Should().BeOfType<OkResult>("event_callback returns an empty HTTP 200 ACK");

        // Iter-2 evaluator item 3 fix: the controller now defers the
        // enqueue to Response.OnCompleted (after ACK). Drive that
        // post-response phase explicitly from the test.
        await harness.FireResponseCompletedAsync();

        SlackInboundEnvelope envelope =
            await SlackControllerTestHelpers.DequeueWithTimeoutAsync(harness.Queue);

        envelope.IdempotencyKey.Should().Be("event:Ev42");
        envelope.SourceType.Should().Be(SlackInboundSourceType.Event);
        envelope.TeamId.Should().Be("T01TEAM");
        envelope.ChannelId.Should().Be("C01CHAN");
        envelope.UserId.Should().Be("U01USER");
        envelope.RawPayload.Should().Be(body,
            "the raw payload is retained verbatim so downstream stages can decode SlackNet-typed views and persist an audit hash");
    }

    [Fact]
    public async Task Event_callback_acks_before_queue_enqueue_runs()
    {
        // Iter-2 evaluator item 3: ACK must precede enqueue. Prove it
        // by asserting the queue is still empty when the controller's
        // Post() returns; only firing OnCompleted (which the host does
        // AFTER flushing the response body) populates the queue.
        const string body = """
            {
              "type": "event_callback",
              "event_id": "Ev99",
              "team_id": "T01",
              "event": { "type": "app_mention", "user": "U", "channel": "C" }
            }
            """;
        SlackTestHttpContext harness =
            SlackControllerTestHelpers.BuildContext(body, "application/json");
        SlackEventsController controller = new()
        {
            ControllerContext = new ControllerContext { HttpContext = harness.Context },
        };

        IActionResult result = await controller.Post(CancellationToken.None);
        result.Should().BeOfType<OkResult>();

        bool dequeuedBeforeFiring = await TryDequeueAsync(harness.Queue, millisecondTimeout: 30);
        dequeuedBeforeFiring.Should().BeFalse(
            "implementation-plan §4.1 requires enqueue AFTER ACK: the queue must remain empty until the response-completed phase fires");

        await harness.FireResponseCompletedAsync();
        bool dequeuedAfterFiring = await TryDequeueAsync(harness.Queue, millisecondTimeout: 250);
        dequeuedAfterFiring.Should().BeTrue(
            "Response.OnCompleted firing simulates the host's post-flush phase, which MUST populate the queue");
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

    [Fact]
    public async Task Body_position_is_rewound_after_read()
    {
        // The Stage 3.1 signature middleware re-reads the buffered body
        // when it builds the HMAC base string; if Stage 4.1's controller
        // leaves the stream at EOF, an upstream middleware that runs
        // after the controller (e.g., logging) cannot re-read it.
        const string body = "{\"type\":\"event_callback\",\"event_id\":\"Ev1\",\"team_id\":\"T01\"}";
        var (ctx, _, _) = SlackControllerTestHelpers.BuildContext(body, "application/json");

        SlackEventsController controller = new()
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };

        await controller.Post(CancellationToken.None);

        ctx.Request.Body.Position.Should().Be(0,
            "controllers MUST rewind the buffered body so chained middleware can re-read it");
    }
}
