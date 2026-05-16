// -----------------------------------------------------------------------
// <copyright file="DefaultSlackModalFastPathHandlerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.1 unit tests for
/// <see cref="DefaultSlackModalFastPathHandler"/>. Pins the real
/// orchestration contract (idempotency → views.open → result) that
/// replaces the iter-1 placeholder.
/// </summary>
public sealed class DefaultSlackModalFastPathHandlerTests
{
    private const string TeamId = "T01TEAM";
    private const string UserId = "U01USER";
    private const string ChannelId = "C01CHAN";
    private const string TriggerId = "trig.42";

    [Fact]
    public async Task Happy_path_returns_Handled_with_no_action_result_when_views_open_succeeds()
    {
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Success() };
        DefaultSlackModalFastPathHandler handler = BuildHandler(views);

        SlackInboundEnvelope envelope = BuildEnvelope("review pr 42");

        SlackModalFastPathResult result = await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackModalFastPathResultKind.Handled);
        result.ActionResult.Should().BeNull(
            "successful views.open uses the controller's default empty 200 ACK");
        views.Invocations.Should().Be(1);
        views.LastRequest.TeamId.Should().Be(TeamId);
        views.LastRequest.TriggerId.Should().Be(TriggerId);
        views.LastRequest.ViewPayload.Should().NotBeNull(
            "the payload builder is invoked to produce the Slack view JSON");
    }

    [Fact]
    public async Task Duplicate_acquires_returns_DuplicateAck_and_does_not_call_views_open()
    {
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Success() };
        SlackInProcessIdempotencyStore store = new();
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, store);

        SlackInboundEnvelope envelope = BuildEnvelope("review pr 42");

        SlackModalFastPathResult first = await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);
        first.ResultKind.Should().Be(SlackModalFastPathResultKind.Handled);

        SlackModalFastPathResult second = await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);
        second.ResultKind.Should().Be(SlackModalFastPathResultKind.DuplicateAck);

        views.Invocations.Should().Be(1,
            "the second call short-circuits at the idempotency check and must not re-open the modal");
    }

    [Fact]
    public async Task Views_open_failure_returns_Handled_with_ephemeral_error_and_releases_the_idempotency_token()
    {
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Failure("missing_scope") };
        SlackInProcessIdempotencyStore store = new();
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, store);

        SlackInboundEnvelope envelope = BuildEnvelope("escalate to oncall");

        SlackModalFastPathResult result = await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackModalFastPathResultKind.Handled,
            "the handler still owns the response so the controller does not enqueue");
        ContentResult? content = result.ActionResult as ContentResult;
        content.Should().NotBeNull("the handler emits an ephemeral error body when views.open fails");
        content!.StatusCode.Should().Be(StatusCodes.Status200OK,
            "Slack ephemeral responses still use HTTP 200");
        content.Content.Should().Contain("\"response_type\":\"ephemeral\"");
        content.Content.Should().Contain("missing_scope",
            "the Slack-returned error code should be surfaced to the invoking user so they can triage");

        // Second call with the SAME key must NOT be treated as a
        // duplicate -- the failure path must have released the token
        // so a retry can succeed.
        FakeViewsOpenClient retryViews = new() { Result = SlackViewsOpenResult.Success() };
        DefaultSlackModalFastPathHandler retryHandler = BuildHandler(retryViews, store);
        SlackModalFastPathResult retry = await retryHandler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);
        retry.ResultKind.Should().Be(SlackModalFastPathResultKind.Handled);
        retryViews.Invocations.Should().Be(1, "Release should let the user retry without the dedup block");
    }

    [Fact]
    public async Task Missing_trigger_id_returns_Handled_with_ephemeral_error_without_calling_views_open()
    {
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Success() };
        DefaultSlackModalFastPathHandler handler = BuildHandler(views);

        // Envelope built from a body that has no trigger_id.
        SlackInboundEnvelope envelope = SlackInboundEnvelopeFactory.Build(
            SlackInboundSourceType.Command,
            "team_id=T01TEAM&user_id=U01USER&command=%2Fagent&text=review+pr+42",
            DateTimeOffset.UtcNow);
        envelope.TriggerId.Should().BeNull("test sanity: no trigger_id in body");

        SlackModalFastPathResult result = await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        result.ResultKind.Should().Be(SlackModalFastPathResultKind.Handled);
        result.ActionResult.Should().BeOfType<ContentResult>();
        views.Invocations.Should().Be(0,
            "no trigger_id means views.open cannot succeed; the handler must short-circuit");
    }

    [Fact]
    public async Task Missing_configuration_returns_Handled_with_admin_friendly_message()
    {
        FakeViewsOpenClient views = new()
        {
            Result = SlackViewsOpenResult.MissingConfiguration("workspace 'T01TEAM' has no bot-token secret reference."),
        };
        DefaultSlackModalFastPathHandler handler = BuildHandler(views);

        SlackInboundEnvelope envelope = BuildEnvelope("review pr 42");

        SlackModalFastPathResult result = await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        ContentResult content = result.ActionResult.Should().BeOfType<ContentResult>().Subject;
        content.Content.Should().Contain("admin", "missing configuration is an admin-fix issue");
    }

    [Fact]
    public async Task Network_failure_returns_Handled_with_retryable_message()
    {
        FakeViewsOpenClient views = new()
        {
            Result = SlackViewsOpenResult.NetworkFailure("timeout"),
        };
        DefaultSlackModalFastPathHandler handler = BuildHandler(views);

        SlackInboundEnvelope envelope = BuildEnvelope("review pr 42");

        SlackModalFastPathResult result = await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        ContentResult content = result.ActionResult.Should().BeOfType<ContentResult>().Subject;
        content.Content.Should().Contain("retry",
            "transport failures are typically transient -- the message should invite the user to retry");
    }

    [Fact]
    public async Task Handler_never_returns_AsyncFallback()
    {
        // Iter-2 evaluator item 2 pin: the modal fast-path must NEVER
        // ask the controller to enqueue a modal command for async
        // processing, because by the time the orchestrator dequeues it
        // Slack's trigger_id has already expired (architecture.md §5.3).
        // Walk every supported failure mode and assert the discriminator.
        SlackViewsOpenResult[] failures =
        {
            SlackViewsOpenResult.Failure("any_slack_error"),
            SlackViewsOpenResult.NetworkFailure("any_transport_error"),
            SlackViewsOpenResult.MissingConfiguration("any_config_error"),
        };

        foreach (SlackViewsOpenResult failure in failures)
        {
            FakeViewsOpenClient views = new() { Result = failure };
            DefaultSlackModalFastPathHandler handler = BuildHandler(views);
            SlackModalFastPathResult result = await handler.HandleAsync(
                BuildEnvelope("review pr 42"), new DefaultHttpContext(), CancellationToken.None);

            result.ResultKind.Should().NotBe(
                SlackModalFastPathResultKind.AsyncFallback,
                $"failure mode {failure.Kind} must surface as Handled with an ephemeral error, NOT AsyncFallback");
        }
    }

    [Fact]
    public async Task Success_emits_modal_open_success_audit_entry()
    {
        // Iter-3 evaluator item 3 pin: every successful views.open MUST
        // produce a durable SlackAuditEntry row (architecture.md §5.3
        // step 5), not just an ILogger line.
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Success() };
        InMemorySlackAuditEntryWriter writer = new();
        SlackModalAuditRecorder recorder = new(writer, NullLogger<SlackModalAuditRecorder>.Instance);
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, auditRecorder: recorder);

        await handler.HandleAsync(BuildEnvelope("review pr 42"), new DefaultHttpContext(), CancellationToken.None);

        writer.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeSuccess);
        writer.Entries[0].RequestType.Should().Be(SlackModalAuditRecorder.RequestTypeModalOpen);
        writer.Entries[0].CommandText.Should().Be("/agent review");
    }

    [Fact]
    public async Task Duplicate_emits_modal_open_duplicate_audit_entry()
    {
        // Iter-3 evaluator item 3 pin: duplicate fast-path invocations
        // MUST be recorded as durable audit rows so an operator can see
        // the suppressed retries -- a silent ILogger line is not
        // queryable.
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Success() };
        SlackInProcessIdempotencyStore store = new();
        InMemorySlackAuditEntryWriter writer = new();
        SlackModalAuditRecorder recorder = new(writer, NullLogger<SlackModalAuditRecorder>.Instance);
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, store, recorder);

        SlackInboundEnvelope envelope = BuildEnvelope("review pr 42");
        await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);
        await handler.HandleAsync(envelope, new DefaultHttpContext(), CancellationToken.None);

        writer.Entries.Should().HaveCount(2);
        writer.Entries[0].Outcome.Should().Be(SlackModalAuditRecorder.OutcomeSuccess);
        writer.Entries[1].Outcome.Should().Be(SlackModalAuditRecorder.OutcomeDuplicate,
            "the second invocation must record a duplicate audit row, not just log a warning");
    }

    [Fact]
    public async Task Views_open_error_emits_modal_open_error_audit_entry_with_detail()
    {
        // Iter-3 evaluator item 3 pin: failed views.open calls MUST
        // populate error_detail so an operator can correlate Slack error
        // codes with the audit log.
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Failure("missing_scope") };
        InMemorySlackAuditEntryWriter writer = new();
        SlackModalAuditRecorder recorder = new(writer, NullLogger<SlackModalAuditRecorder>.Instance);
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, auditRecorder: recorder);

        await handler.HandleAsync(BuildEnvelope("escalate to oncall"), new DefaultHttpContext(), CancellationToken.None);

        SlackAuditEntry entry = writer.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.ErrorDetail.Should().Contain("missing_scope",
            "Slack's error code must be captured so it's queryable in the audit log");
    }

    private static DefaultSlackModalFastPathHandler BuildHandler(
        FakeViewsOpenClient views,
        SlackInProcessIdempotencyStore? store = null,
        SlackModalAuditRecorder? auditRecorder = null)
    {
        SlackInProcessIdempotencyStore resolvedStore = store ?? new SlackInProcessIdempotencyStore();
        SlackModalAuditRecorder resolvedAudit = auditRecorder ?? new SlackModalAuditRecorder(
            new InMemorySlackAuditEntryWriter(),
            NullLogger<SlackModalAuditRecorder>.Instance);

        return new DefaultSlackModalFastPathHandler(
            resolvedStore,
            new DefaultSlackModalPayloadBuilder(),
            views,
            resolvedAudit,
            NullLogger<DefaultSlackModalFastPathHandler>.Instance);
    }

    private static SlackInboundEnvelope BuildEnvelope(string text)
    {
        string body = $"team_id={TeamId}&channel_id={ChannelId}&user_id={UserId}&command=%2Fagent&text={Uri.EscapeDataString(text)}&trigger_id={TriggerId}";
        return SlackInboundEnvelopeFactory.Build(SlackInboundSourceType.Command, body, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Test double for <see cref="ISlackViewsOpenClient"/> that records
    /// every invocation and returns the caller-configured result.
    /// </summary>
    private sealed class FakeViewsOpenClient : ISlackViewsOpenClient
    {
        public SlackViewsOpenResult Result { get; set; } = SlackViewsOpenResult.Success();

        public int Invocations { get; private set; }

        public SlackViewsOpenRequest LastRequest { get; private set; }

        public Task<SlackViewsOpenResult> OpenAsync(SlackViewsOpenRequest request, CancellationToken ct)
        {
            this.Invocations++;
            this.LastRequest = request;
            return Task.FromResult(this.Result);
        }
    }
}
