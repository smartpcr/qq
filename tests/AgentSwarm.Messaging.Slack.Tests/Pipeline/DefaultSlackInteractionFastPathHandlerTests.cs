// -----------------------------------------------------------------------
// <copyright file="DefaultSlackInteractionFastPathHandlerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 5.3 iter-2 evaluator item #2 (STRUCTURAL fix) tests for the
/// synchronous interaction fast-path. Pins the contract that
/// RequiresComment button clicks open the comment modal INLINE (before
/// the controller flushes the ACK) and that downstream failure modes
/// surface an ephemeral error to the user rather than silently
/// dropping the click.
/// </summary>
public sealed class DefaultSlackInteractionFastPathHandlerTests
{
    [Fact]
    public async Task RequiresComment_button_click_opens_views_open_inline_and_skips_enqueue()
    {
        RecordingViewsOpenClient views = new();
        RecordingMessageRenderer renderer = new();
        RecordingThreadMappingLookup lookup = new();
        RecordingFastPathIdempotencyStore idempotency = new();
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            renderer,
            lookup,
            idempotency,
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-FP", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000FP",
            userId: "U1",
            triggerId: "trig-fp");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-fp");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled,
            "the fast-path MUST claim ownership when it opens views.open so the controller skips the async enqueue (the trigger_id is consumed)");
        result.ActionResult.Should().BeNull("success path returns a bare HTTP 200");
        views.Requests.Should().ContainSingle();
        views.Requests[0].TriggerId.Should().Be("trig-fp");
        views.Requests[0].TeamId.Should().Be(envelope.TeamId);
        renderer.LastContext.Should().NotBeNull();
        renderer.LastContext!.Value.QuestionId.Should().Be("Q-FP");
        renderer.LastContext!.Value.ActionValue.Should().Be("request-changes");
    }

    [Fact]
    public async Task Non_block_actions_payload_returns_async_fallback()
    {
        DefaultSlackInteractionFastPathHandler handler = new(
            new RecordingViewsOpenClient(),
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string payloadJson = JsonSerializer.Serialize(new
        {
            type = "view_submission",
            team = new { id = "T1" },
            user = new { id = "U1" },
            view = new { id = "V1", callback_id = "agent_review_modal" },
        });
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-view");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.AsyncFallback,
            "view_submission payloads MUST defer to the async handler -- the fast-path only owns block_actions with RequiresComment buttons");
    }

    [Fact]
    public async Task Plain_button_click_without_requires_comment_returns_async_fallback()
    {
        RecordingViewsOpenClient views = new();
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-PLAIN", requiresComment: false);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "approve",
            actionLabel: "Approve",
            channelId: "C1",
            messageTs: "1700000000.000PL",
            userId: "U1",
            triggerId: "trig-plain");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-plain");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.AsyncFallback,
            "plain button clicks (no RequiresComment) MUST defer to the async handler -- no trigger_id-bound side-effect to race");
        views.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Views_open_failure_returns_handled_with_ephemeral_error_and_skips_enqueue()
    {
        RecordingViewsOpenClient views = new()
        {
            NextResult = SlackViewsOpenResult.NetworkFailure("simulated"),
        };
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-VFAIL", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000VF",
            userId: "U1",
            triggerId: "trig-vfail");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-vfail");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled,
            "a failed views.open MUST still claim Handled so the async path does not retry against an already-expired trigger_id");
        result.ActionResult.Should().BeOfType<ContentResult>(
            "the user MUST see an ephemeral error explaining why the comment dialog did not open -- silent drops were the iter-1 regression");
    }

    [Fact]
    public async Task Missing_trigger_id_returns_handled_with_ephemeral_error()
    {
        DefaultSlackInteractionFastPathHandler handler = new(
            new RecordingViewsOpenClient(),
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-NOTRG", requiresComment: true);
        string payloadJson = JsonSerializer.Serialize(new
        {
            type = SlackInteractionHandler.BlockActionsType,
            team = new { id = "T1" },
            user = new { id = "U1" },
            channel = new { id = "C1" },
            message = new { ts = "1700000000.000NT" },
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

        // Envelope's TriggerId is also null so the fast-path cannot recover one.
        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "int:T1:U1:notrg",
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: "payload=" + Uri.EscapeDataString(payloadJson),
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled);
        result.ActionResult.Should().BeOfType<ContentResult>(
            "the user MUST be told the click cannot complete because Slack did not deliver a trigger_id; the click MUST NOT be enqueued because the async path also cannot open views.open without one");
    }

    [Fact]
    public async Task Unknown_block_id_returns_async_fallback()
    {
        DefaultSlackInteractionFastPathHandler handler = new(
            new RecordingViewsOpenClient(),
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string payloadJson = BuildBlockActionsPayload(
            blockId: "third_party_block",
            actionValue: "x",
            actionLabel: "X",
            channelId: "C1",
            messageTs: "1700000000.000UNK",
            userId: "U1",
            triggerId: "trig-unk");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-unk");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.AsyncFallback,
            "non-question block_ids MUST defer to the async handler -- the fast-path only owns Stage 5.3 RequiresComment buttons");
    }

    // -----------------------------------------------------------------
    // Stage 5.3 iter-3 evaluator item #4 (STRUCTURAL fix).
    //
    // Before: ResolveCorrelationIdAsync caught the lookup exception and
    // returned envelope.IdempotencyKey as the fallback correlation id;
    // the modal payload then pinned that fallback into
    // private_metadata.correlationId, which the async
    // SlackInteractionHandler trusts when the eventual view_submission
    // arrives. Net effect: a DB outage produced wrong-correlation
    // RequiresComment decisions even though the iter-1 item-#2 fix had
    // already addressed the same bug on the plain button-click path.
    //
    // After: the lookup exception propagates; HandleAsync wraps the
    // call and returns an ephemeral error WITHOUT invoking the
    // renderer or views.open. The trigger_id is wasted, the user
    // retries -- but no wrong-correlation decision is ever published.
    // -----------------------------------------------------------------
    [Fact]
    public async Task RequiresComment_click_propagates_thread_mapping_lookup_failure_as_ephemeral_error()
    {
        RecordingViewsOpenClient views = new();
        RecordingMessageRenderer renderer = new();
        RecordingThreadMappingLookup lookup = new()
        {
            ThrowOnLookup = new InvalidOperationException("DB outage simulated by test."),
        };
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            renderer,
            lookup,
            new RecordingFastPathIdempotencyStore(),
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-DBDOWN", requiresComment: true);
        string payloadJson = BuildBlockActionsPayloadWithThread(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000DB",
            threadTs: "1700000000.000ROOT",
            userId: "U1",
            triggerId: "trig-dbdown");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-dbdown");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled,
            "DB failures during the fast-path MUST claim Handled so the async path does not redundantly process the click against a now-expired trigger_id");
        result.ActionResult.Should().BeOfType<ContentResult>(
            "DB failures during correlation-id resolution MUST surface as an ephemeral error -- silently pinning the envelope idempotency key would publish a wrong-correlation decision when the modal is submitted");
        views.Requests.Should().BeEmpty(
            "the fast-path MUST NOT open a comment modal whose private_metadata would carry a fallback correlation id pointing at the envelope's idempotency key");
        renderer.LastContext.Should().BeNull(
            "the renderer MUST NOT be invoked because the resulting modal would carry a wrong correlation id");
    }

    // -----------------------------------------------------------------
    // Stage 5.3 iter-4 evaluator item #1 (STRUCTURAL fix). The
    // fast-path now reserves envelope.IdempotencyKey via
    // ISlackFastPathIdempotencyStore BEFORE opening views.open so
    // Slack retries of the same RequiresComment click (same
    // trigger_id => same idempotency key per
    // SlackInboundEnvelopeFactory) cannot open duplicate comment
    // modals or produce duplicate HumanDecisionEvent rows when the
    // user submits one of them. The pattern mirrors
    // DefaultSlackModalFastPathHandler's iter-5 fix:
    //   * Duplicate -> silent ACK, NO views.open, NO enqueue
    //   * StoreUnavailable -> proceed degraded (the in-process L1
    //     still gates same-process retries)
    //   * On every failure path (lookup throw, renderer throw,
    //     views.open throw, views.open non-success) -> ReleaseAsync
    //     so a retry can take a fresh reservation
    //   * On views.open success -> MarkCompletedAsync (best-effort)
    //     so the durable row flips reserved -> modal_opened and the
    //     ingestor skips it on the retry window
    // -----------------------------------------------------------------
    [Fact]
    public async Task Duplicate_idempotency_key_returns_handled_and_skips_views_open_and_renderer()
    {
        RecordingViewsOpenClient views = new();
        RecordingMessageRenderer renderer = new();
        RecordingThreadMappingLookup lookup = new();
        RecordingFastPathIdempotencyStore idempotency = new()
        {
            NextAcquireResult = SlackFastPathIdempotencyResult.Duplicate("slack retry observed"),
        };
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            renderer,
            lookup,
            idempotency,
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-DUP", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000DUP",
            userId: "U1",
            triggerId: "trig-dup");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-dup");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled,
            "a duplicate Slack retry MUST be claimed by the fast-path so the controller does not enqueue a second envelope");
        result.ActionResult.Should().BeNull(
            "duplicate detection returns a bare HTTP 200 (no ephemeral error -- the original attempt already showed any user-visible result)");
        views.Requests.Should().BeEmpty(
            "a duplicate retry MUST NOT re-open the comment modal -- Slack would show two stacked modals to the same user");
        renderer.LastContext.Should().BeNull(
            "rendering MUST be skipped on a duplicate -- the modal payload was already built and sent by the original attempt");
        idempotency.AcquireCalls.Should().ContainSingle();
        idempotency.AcquireCalls[0].Key.Should().Be(envelope.IdempotencyKey);
        idempotency.ReleaseCalls.Should().BeEmpty(
            "duplicate detection MUST NOT release the key -- the original attempt's reservation is the dedup anchor");
        idempotency.MarkCompletedCalls.Should().BeEmpty(
            "duplicate detection MUST NOT mark-completed -- the original attempt owns that transition");
    }

    [Fact]
    public async Task Store_unavailable_proceeds_with_views_open_in_degraded_mode()
    {
        RecordingViewsOpenClient views = new();
        RecordingMessageRenderer renderer = new();
        RecordingThreadMappingLookup lookup = new();
        RecordingFastPathIdempotencyStore idempotency = new()
        {
            NextAcquireResult = SlackFastPathIdempotencyResult.StoreUnavailable("simulated durable failure"),
        };
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            renderer,
            lookup,
            idempotency,
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-DEG", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000DEG",
            userId: "U1",
            triggerId: "trig-deg");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-deg");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled,
            "store-unavailable MUST NOT block the user -- the fast-path proceeds and the in-process L1 still gates same-process retries");
        views.Requests.Should().ContainSingle(
            "views.open MUST still run when the durable store is unavailable -- otherwise a durable outage blocks every Slack interaction");
        renderer.LastContext.Should().NotBeNull();
    }

    [Fact]
    public async Task Successful_views_open_marks_idempotency_key_completed()
    {
        RecordingViewsOpenClient views = new();
        RecordingMessageRenderer renderer = new();
        RecordingThreadMappingLookup lookup = new();
        RecordingFastPathIdempotencyStore idempotency = new();
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            renderer,
            lookup,
            idempotency,
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-OK", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000OK",
            userId: "U1",
            triggerId: "trig-ok");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-ok");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled);
        idempotency.AcquireCalls.Should().ContainSingle();
        idempotency.MarkCompletedCalls.Should().ContainSingle(
            "a successful views.open MUST flip the durable row from reserved -> modal_opened so the Stage 4.3 ingestor skips it on the retry window");
        idempotency.MarkCompletedCalls[0].Should().Be(envelope.IdempotencyKey);
        idempotency.ReleaseCalls.Should().BeEmpty(
            "success path MUST NOT release the key -- the row stays as a dedup anchor for the full retention window");
    }

    [Fact]
    public async Task Views_open_failure_releases_idempotency_key_so_retry_can_succeed()
    {
        RecordingViewsOpenClient views = new()
        {
            NextResult = SlackViewsOpenResult.NetworkFailure("simulated"),
        };
        RecordingFastPathIdempotencyStore idempotency = new();
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            idempotency,
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-RELVF", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000RVF",
            userId: "U1",
            triggerId: "trig-relvf");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-relvf");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled);
        idempotency.ReleaseCalls.Should().ContainSingle(
            "a failed views.open MUST release the reservation -- otherwise the user is locked out of retrying for the full TTL even though no modal opened");
        idempotency.ReleaseCalls[0].Should().Be(envelope.IdempotencyKey);
        idempotency.MarkCompletedCalls.Should().BeEmpty(
            "MarkCompleted is ONLY for successful views.open -- a failed open must not flip the durable row to modal_opened");
    }

    [Fact]
    public async Task Views_open_throw_releases_idempotency_key()
    {
        RecordingViewsOpenClient views = new()
        {
            ThrowOnOpen = new InvalidOperationException("Slack transport blew up."),
        };
        RecordingFastPathIdempotencyStore idempotency = new();
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            idempotency,
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-RELTHR", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000RTH",
            userId: "U1",
            triggerId: "trig-relthr");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-relthr");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled,
            "a views.open throw MUST still claim Handled so the controller does not redundantly enqueue against an expired trigger_id");
        idempotency.ReleaseCalls.Should().ContainSingle();
        idempotency.ReleaseCalls[0].Should().Be(envelope.IdempotencyKey);
    }

    [Fact]
    public async Task Thread_mapping_lookup_failure_releases_idempotency_key()
    {
        RecordingViewsOpenClient views = new();
        RecordingThreadMappingLookup lookup = new()
        {
            ThrowOnLookup = new InvalidOperationException("DB outage simulated by test."),
        };
        RecordingFastPathIdempotencyStore idempotency = new();
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            new RecordingMessageRenderer(),
            lookup,
            idempotency,
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-RELLK", requiresComment: true);
        string payloadJson = BuildBlockActionsPayloadWithThread(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000RLK",
            threadTs: "1700000000.000RLKROOT",
            userId: "U1",
            triggerId: "trig-rellk");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-rellk");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled);
        views.Requests.Should().BeEmpty(
            "the renderer and views.open MUST be skipped when the correlation lookup fails (iter-3 item #4 contract)");
        idempotency.ReleaseCalls.Should().ContainSingle(
            "a lookup failure MUST release the key so the user can retry once the DB recovers -- otherwise the reservation blocks the same click for the full TTL");
        idempotency.ReleaseCalls[0].Should().Be(envelope.IdempotencyKey);
    }

    [Fact]
    public async Task Renderer_exception_releases_idempotency_key()
    {
        RecordingViewsOpenClient views = new();
        RecordingMessageRenderer renderer = new()
        {
            ThrowOnComment = new InvalidOperationException("Renderer blew up."),
        };
        RecordingFastPathIdempotencyStore idempotency = new();
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            renderer,
            new RecordingThreadMappingLookup(),
            idempotency,
            BuildAuditRecorder(),
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-RELRD", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000RRD",
            userId: "U1",
            triggerId: "trig-relrd");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-relrd");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled);
        views.Requests.Should().BeEmpty(
            "views.open MUST be skipped if the renderer failed -- there is no payload to send");
        idempotency.ReleaseCalls.Should().ContainSingle(
            "a renderer exception MUST release the key so the user can retry -- the reservation must not pin a click whose modal never opened");
        idempotency.ReleaseCalls[0].Should().Be(envelope.IdempotencyKey);
    }

    // -----------------------------------------------------------------
    // Stage 5.3 iter-4 evaluator item #2 (STRUCTURAL fix). Every
    // terminal of the fast-path MUST emit a SlackModalAuditRecorder
    // row so the RequiresComment HTTP path that short-circuits the
    // async pipeline (controller.cs:110-119) is observable in the
    // durable audit log. Without these rows the click is invisible
    // to operator dashboards / alerts despite leaving a user-visible
    // side-effect (modal opened or ephemeral error rendered).
    // Pattern mirrors DefaultSlackModalFastPathHandler's audit
    // contract (RecordSuccessAsync / RecordDuplicateAsync /
    // RecordErrorAsync at every terminal).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Successful_views_open_records_modal_open_success_audit_entry()
    {
        (SlackModalAuditRecorder recorder, InMemorySlackAuditEntryWriter writer) = BuildAuditRecorderWithWriter();
        DefaultSlackInteractionFastPathHandler handler = new(
            new RecordingViewsOpenClient(),
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            recorder,
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-AUD-OK", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000AUDOK",
            userId: "U1",
            triggerId: "trig-aud-ok");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-aud-ok");

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackInteractionFastPathResultKind.Handled);
        SlackAuditEntry entry = writer.Entries.Should().ContainSingle(
            "every successful interaction fast-path invocation MUST emit one audit row -- otherwise the click is invisible to the audit log").Subject;
        entry.Direction.Should().Be(SlackModalAuditRecorder.DirectionInbound);
        entry.RequestType.Should().Be(SlackModalAuditRecorder.RequestTypeModalOpen);
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeSuccess);
        entry.CommandText.Should().Be(
            $"/agent {DefaultSlackInteractionFastPathHandler.AuditSubCommand}",
            "the interaction fast-path pins a stable sub_command so audit queries filter cleanly between slash-command and interaction-driven modal opens");
        entry.CorrelationId.Should().Be(envelope.IdempotencyKey);
        entry.TeamId.Should().Be("T1");
        entry.ErrorDetail.Should().BeNull("success entries MUST NOT carry an error_detail");
    }

    [Fact]
    public async Task Duplicate_idempotency_key_records_modal_open_duplicate_audit_entry()
    {
        (SlackModalAuditRecorder recorder, InMemorySlackAuditEntryWriter writer) = BuildAuditRecorderWithWriter();
        RecordingFastPathIdempotencyStore idempotency = new()
        {
            NextAcquireResult = SlackFastPathIdempotencyResult.Duplicate("slack retry observed"),
        };
        DefaultSlackInteractionFastPathHandler handler = new(
            new RecordingViewsOpenClient(),
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            idempotency,
            recorder,
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-AUD-DUP", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000AUDDUP",
            userId: "U1",
            triggerId: "trig-aud-dup");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-aud-dup");

        await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle(
            "duplicate detection MUST emit a single audit row so retries can be reconciled with the original attempt").Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeDuplicate);
        entry.ResponsePayload.Should().Be("slack retry observed",
            "the diagnostic from the idempotency store MUST land in response_payload so operators can see WHY the row was a duplicate");
    }

    [Fact]
    public async Task Missing_trigger_id_records_modal_open_error_audit_entry()
    {
        (SlackModalAuditRecorder recorder, InMemorySlackAuditEntryWriter writer) = BuildAuditRecorderWithWriter();
        DefaultSlackInteractionFastPathHandler handler = new(
            new RecordingViewsOpenClient(),
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            recorder,
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-AUD-NOTRG", requiresComment: true);
        // Payload deliberately omits trigger_id (and the envelope also
        // has no triggerId), so the fast-path takes the missing-
        // trigger ephemeral-error branch.
        string payloadJson = JsonSerializer.Serialize(new
        {
            type = SlackInteractionHandler.BlockActionsType,
            team = new { id = "T1" },
            user = new { id = "U1" },
            channel = new { id = "C1" },
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
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: null);

        SlackInteractionFastPathResult result =
            await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ActionResult.Should().BeOfType<ContentResult>(
            "missing-trigger MUST surface an ephemeral error so the user knows to retry");
        SlackAuditEntry entry = writer.Entries.Should().ContainSingle(
            "every error terminal MUST emit an audit row so the failure is correlatable with the user-visible ephemeral message").Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.ErrorDetail.Should().Be("missing trigger_id; views.open cannot be called.");
    }

    [Fact]
    public async Task Views_open_failure_records_modal_open_error_audit_entry()
    {
        (SlackModalAuditRecorder recorder, InMemorySlackAuditEntryWriter writer) = BuildAuditRecorderWithWriter();
        RecordingViewsOpenClient views = new()
        {
            NextResult = SlackViewsOpenResult.NetworkFailure("simulated 503"),
        };
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            recorder,
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-AUD-VF", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000AUDVF",
            userId: "U1",
            triggerId: "trig-aud-vf");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-aud-vf");

        await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.ErrorDetail.Should().Contain("views_open_NetworkFailure",
            "the views.open failure kind MUST appear in error_detail so operators can distinguish Slack-side errors from transport errors");
        entry.ErrorDetail.Should().Contain("simulated 503",
            "the views.open error message MUST be preserved in error_detail so operators can correlate with Slack's API logs");
    }

    [Fact]
    public async Task Views_open_throw_records_modal_open_error_audit_entry()
    {
        (SlackModalAuditRecorder recorder, InMemorySlackAuditEntryWriter writer) = BuildAuditRecorderWithWriter();
        RecordingViewsOpenClient views = new()
        {
            ThrowOnOpen = new InvalidOperationException("Slack transport blew up."),
        };
        DefaultSlackInteractionFastPathHandler handler = new(
            views,
            new RecordingMessageRenderer(),
            new RecordingThreadMappingLookup(),
            new RecordingFastPathIdempotencyStore(),
            recorder,
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-AUD-VT", requiresComment: true);
        string payloadJson = BuildBlockActionsPayload(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000AUDVT",
            userId: "U1",
            triggerId: "trig-aud-vt");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-aud-vt");

        await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.ErrorDetail.Should().Contain("views_open_threw",
            "thrown exceptions MUST be tagged distinctly from views.open's structured failure kinds so operators can spot transport bugs vs Slack-side errors");
        entry.ErrorDetail.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public async Task Thread_mapping_lookup_failure_records_modal_open_error_audit_entry()
    {
        (SlackModalAuditRecorder recorder, InMemorySlackAuditEntryWriter writer) = BuildAuditRecorderWithWriter();
        RecordingThreadMappingLookup lookup = new()
        {
            ThrowOnLookup = new InvalidOperationException("DB outage simulated by test."),
        };
        DefaultSlackInteractionFastPathHandler handler = new(
            new RecordingViewsOpenClient(),
            new RecordingMessageRenderer(),
            lookup,
            new RecordingFastPathIdempotencyStore(),
            recorder,
            NullLogger<DefaultSlackInteractionFastPathHandler>.Instance);

        string blockId = SlackInteractionEncoding.EncodeQuestionBlockId("Q-AUD-LKF", requiresComment: true);
        string payloadJson = BuildBlockActionsPayloadWithThread(
            blockId: blockId,
            actionValue: "request-changes",
            actionLabel: "Request changes",
            channelId: "C1",
            messageTs: "1700000000.000AUDLKF",
            threadTs: "1700000000.000AUDLKFROOT",
            userId: "U1",
            triggerId: "trig-aud-lkf");
        SlackInboundEnvelope envelope = BuildEnvelope(
            payload: "payload=" + Uri.EscapeDataString(payloadJson),
            triggerId: "trig-aud-lkf");

        await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.ErrorDetail.Should().Contain("thread_mapping_lookup_failed");
        entry.ErrorDetail.Should().Contain("InvalidOperationException");
    }

    private static SlackModalAuditRecorder BuildAuditRecorder()
        => new(new InMemorySlackAuditEntryWriter(), NullLogger<SlackModalAuditRecorder>.Instance);

    private static (SlackModalAuditRecorder Recorder, InMemorySlackAuditEntryWriter Writer) BuildAuditRecorderWithWriter()
    {
        InMemorySlackAuditEntryWriter writer = new();
        return (new SlackModalAuditRecorder(writer, NullLogger<SlackModalAuditRecorder>.Instance), writer);
    }

    private static string BuildBlockActionsPayload(
        string blockId,
        string actionValue,
        string actionLabel,
        string channelId,
        string messageTs,
        string userId,
        string triggerId)
    {
        var payload = new
        {
            type = SlackInteractionHandler.BlockActionsType,
            trigger_id = triggerId,
            team = new { id = "T1" },
            user = new { id = userId },
            channel = new { id = channelId },
            message = new { ts = messageTs },
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

    private static string BuildBlockActionsPayloadWithThread(
        string blockId,
        string actionValue,
        string actionLabel,
        string channelId,
        string messageTs,
        string threadTs,
        string userId,
        string triggerId)
    {
        var payload = new
        {
            type = SlackInteractionHandler.BlockActionsType,
            trigger_id = triggerId,
            team = new { id = "T1" },
            user = new { id = userId },
            channel = new { id = channelId },
            message = new { ts = messageTs, thread_ts = threadTs },
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

    private static SlackInboundEnvelope BuildEnvelope(string payload, string? triggerId)
        => new(
            IdempotencyKey: "int:T1:U1:" + (triggerId ?? "no-trg"),
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: payload,
            TriggerId: triggerId,
            ReceivedAt: DateTimeOffset.UtcNow);

    private sealed class RecordingViewsOpenClient : ISlackViewsOpenClient
    {
        public List<SlackViewsOpenRequest> Requests { get; } = new();

        public SlackViewsOpenResult NextResult { get; set; } = SlackViewsOpenResult.Success();

        public Exception? ThrowOnOpen { get; set; }

        public Task<SlackViewsOpenResult> OpenAsync(SlackViewsOpenRequest request, CancellationToken ct)
        {
            this.Requests.Add(request);
            if (this.ThrowOnOpen is not null)
            {
                throw this.ThrowOnOpen;
            }

            return Task.FromResult(this.NextResult);
        }
    }

    private sealed class RecordingMessageRenderer : ISlackMessageRenderer
    {
        public SlackCommentModalContext? LastContext { get; private set; }

        public Exception? ThrowOnComment { get; set; }

        public object RenderReviewModal(SlackReviewModalContext context)
            => new { type = "modal", callback_id = "agent_review_modal" };

        public object RenderEscalateModal(SlackEscalateModalContext context)
            => new { type = "modal", callback_id = "agent_escalate_modal" };

        public object RenderCommentModal(SlackCommentModalContext context)
        {
            if (this.ThrowOnComment is not null)
            {
                throw this.ThrowOnComment;
            }

            this.LastContext = context;
            return new { type = "modal", callback_id = "agent_comment_modal" };
        }
    }

    private sealed class RecordingThreadMappingLookup : ISlackThreadMappingLookup
    {
        public List<(string Team, string? Channel, string? Thread)> Lookups { get; } = new();

        public SlackThreadMapping? NextMapping { get; set; }

        public Exception? ThrowOnLookup { get; set; }

        public Task<SlackThreadMapping?> LookupAsync(
            string teamId,
            string? channelId,
            string? threadTs,
            CancellationToken ct)
        {
            this.Lookups.Add((teamId, channelId, threadTs));
            if (this.ThrowOnLookup is not null)
            {
                throw this.ThrowOnLookup;
            }

            return Task.FromResult(this.NextMapping);
        }
    }

    private sealed class RecordingFastPathIdempotencyStore : ISlackFastPathIdempotencyStore
    {
        public List<(string Key, SlackInboundEnvelope Envelope, TimeSpan? Lifetime)> AcquireCalls { get; } = new();

        public List<string> ReleaseCalls { get; } = new();

        public List<string> MarkCompletedCalls { get; } = new();

        public SlackFastPathIdempotencyResult NextAcquireResult { get; set; } = SlackFastPathIdempotencyResult.Acquired();

        public Exception? ThrowOnMarkCompleted { get; set; }

        public ValueTask<SlackFastPathIdempotencyResult> TryAcquireAsync(
            string key,
            SlackInboundEnvelope envelope,
            TimeSpan? lifetime = null,
            CancellationToken ct = default)
        {
            this.AcquireCalls.Add((key, envelope, lifetime));
            return new ValueTask<SlackFastPathIdempotencyResult>(this.NextAcquireResult);
        }

        public ValueTask ReleaseAsync(string key, CancellationToken ct = default)
        {
            this.ReleaseCalls.Add(key);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkCompletedAsync(string key, CancellationToken ct = default)
        {
            this.MarkCompletedCalls.Add(key);
            if (this.ThrowOnMarkCompleted is not null)
            {
                throw this.ThrowOnMarkCompleted;
            }

            return ValueTask.CompletedTask;
        }
    }
}
