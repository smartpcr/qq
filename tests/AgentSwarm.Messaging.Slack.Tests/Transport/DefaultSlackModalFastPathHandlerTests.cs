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

    [Fact]
    public async Task Success_calls_MarkCompletedAsync_on_the_idempotency_store_to_flip_durable_row_to_modal_opened()
    {
        // Iter-4 fix: the iter-3 handler invoked TryAcquireAsync (which
        // inserts the durable slack_inbound_request_record row with
        // processing_status='reserved') but NEVER invoked
        // MarkCompletedAsync on success, so every row leaked at
        // 'reserved' forever. Stage 4.3's async ingestor would then
        // mis-route those rows (either replay them or wait indefinitely
        // for a reservation owner that already terminated). Pin the
        // success-path call so this regression cannot reappear.
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Success() };
        RecordingIdempotencyStore store = new() { Plan = SlackFastPathIdempotencyResult.Acquired() };
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, idempotencyStore: store);

        await handler.HandleAsync(BuildEnvelope("review pr 42"), new DefaultHttpContext(), CancellationToken.None);

        store.MarkCompletedCalls.Should().ContainSingle(
            "successful views.open MUST flip the durable row from 'reserved' to 'modal_opened'");
        store.ReleaseCalls.Should().BeEmpty(
            "the success path must NOT delete the durable row -- it stays as the dedup anchor for the retention window");
    }

    [Fact]
    public async Task Views_open_failure_calls_ReleaseAsync_not_MarkCompletedAsync()
    {
        // Iter-4 fix: the failure path must Release (delete the row) so
        // a retry can take a fresh reservation; it must NOT
        // MarkCompleted (which would preserve the row and silently
        // ACK every future retry as a duplicate).
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Failure("internal_error") };
        RecordingIdempotencyStore store = new() { Plan = SlackFastPathIdempotencyResult.Acquired() };
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, idempotencyStore: store);

        await handler.HandleAsync(BuildEnvelope("review pr 42"), new DefaultHttpContext(), CancellationToken.None);

        store.MarkCompletedCalls.Should().BeEmpty(
            "the failure path must NOT mark the reservation completed -- the user must be able to retry");
        store.ReleaseCalls.Should().ContainSingle(
            "the failure path MUST release the reservation so a retry can succeed");
    }

    [Fact]
    public async Task Success_swallows_MarkCompletedAsync_exception_so_caller_does_not_see_failure()
    {
        // Iter-4 evaluator item 2 pin: ISlackFastPathIdempotencyStore
        // §101-103 says the MarkCompletedAsync call MUST be best-effort
        // and MUST NOT throw to the caller once views.open has
        // succeeded -- the user-visible modal is already open and the
        // controller has nothing useful to do with a late exception.
        // Pin the handler's try/catch so a future refactor that drops
        // it surfaces here, not as a 5xx after a successful modal open.
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Success() };
        RecordingIdempotencyStore store = new()
        {
            Plan = SlackFastPathIdempotencyResult.Acquired(),
            MarkCompletedThrow = new InvalidOperationException("L2 store is down."),
        };
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, idempotencyStore: store);

        // No try/catch -- if HandleAsync rethrows the inner exception
        // the test fails naturally. The success path is contractually
        // required to swallow the MarkCompletedAsync failure.
        SlackModalFastPathResult result = await handler.HandleAsync(
            BuildEnvelope("review pr 42"),
            new DefaultHttpContext(),
            CancellationToken.None);

        result.ResultKind.Should().Be(
            SlackModalFastPathResultKind.Handled,
            "views.open succeeded -- the handler must surface Handled even when MarkCompletedAsync subsequently throws");
        store.MarkCompletedCalls.Should().ContainSingle(
            "the handler still INVOKES MarkCompletedAsync; it only suppresses the thrown exception");
        views.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Success_calls_MarkCompletedAsync_with_uncancellable_token_even_when_request_token_was_cancelled()
    {
        // Iter-4 evaluator item 2 pin: when the request-scope
        // CancellationToken is cancelled during the best-effort
        // durable status flip, the durable store
        // (CompositeSlackFastPathIdempotencyStore.MarkCompletedAsync)
        // rethrows OperationCanceledException -- which would propagate
        // through HandleAsync if the handler passed the request token.
        // The fix is two layers of defense: pass CancellationToken.None
        // to MarkCompletedAsync, AND wrap it in try/catch. Pin BOTH so
        // a future refactor that drops either one surfaces here.
        FakeViewsOpenClient views = new() { Result = SlackViewsOpenResult.Success() };
        RecordingIdempotencyStore store = new() { Plan = SlackFastPathIdempotencyResult.Acquired() };
        DefaultSlackModalFastPathHandler handler = BuildHandler(views, idempotencyStore: store);

        using CancellationTokenSource alreadyCancelled = new();
        alreadyCancelled.Cancel();

        SlackModalFastPathResult result = await handler.HandleAsync(
            BuildEnvelope("escalate to oncall"),
            new DefaultHttpContext(),
            alreadyCancelled.Token);

        result.ResultKind.Should().Be(
            SlackModalFastPathResultKind.Handled,
            "an already-cancelled request token must not cause the success path to throw -- the modal is open in Slack");
        store.MarkCompletedCalls.Should().ContainSingle(
            "the handler must STILL invoke MarkCompletedAsync so the durable row transitions out of 'reserved'");
        store.MarkCompletedTokens.Should().ContainSingle()
            .Which.CanBeCanceled.Should().BeFalse(
                "the handler MUST pass CancellationToken.None (which has CanBeCanceled = false) so the durable store cannot see the cancelled request token and rethrow OperationCanceledException");
    }

    private static DefaultSlackModalFastPathHandler BuildHandler(
        FakeViewsOpenClient views,
        SlackInProcessIdempotencyStore? store = null,
        SlackModalAuditRecorder? auditRecorder = null,
        ISlackFastPathIdempotencyStore? idempotencyStore = null)
    {
        ISlackFastPathIdempotencyStore resolvedStore =
            idempotencyStore ?? store ?? new SlackInProcessIdempotencyStore();
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

    /// <summary>
    /// Recording <see cref="ISlackFastPathIdempotencyStore"/> double
    /// used by the iter-4 success/failure-path pins to assert which of
    /// <see cref="ISlackFastPathIdempotencyStore.ReleaseAsync"/> and
    /// <see cref="ISlackFastPathIdempotencyStore.MarkCompletedAsync"/>
    /// the handler invokes. Iter-5 extends it with a captured-token
    /// list and an optional throw-from-MarkCompleted toggle so the
    /// iter-4 evaluator-item-2 pins can assert the handler passes
    /// <see cref="CancellationToken.None"/> AND swallows late
    /// exceptions on the success path.
    /// </summary>
    private sealed class RecordingIdempotencyStore : ISlackFastPathIdempotencyStore
    {
        public SlackFastPathIdempotencyResult Plan { get; set; } = SlackFastPathIdempotencyResult.Acquired();

        public List<string> AcquireCalls { get; } = new();

        public List<string> ReleaseCalls { get; } = new();

        public List<string> MarkCompletedCalls { get; } = new();

        /// <summary>
        /// Captures the <see cref="CancellationToken"/> the handler
        /// passes to every <see cref="MarkCompletedAsync"/> invocation.
        /// Used by the iter-5 pin to assert
        /// <see cref="CancellationToken.None"/> is forwarded (the
        /// uncancellable token has
        /// <see cref="CancellationToken.CanBeCanceled"/> = false).
        /// </summary>
        public List<CancellationToken> MarkCompletedTokens { get; } = new();

        /// <summary>
        /// When set, <see cref="MarkCompletedAsync"/> throws this
        /// exception synchronously. The iter-5 pin uses this to
        /// reproduce the "L2 store unavailable AFTER the modal opened"
        /// failure mode that ISlackFastPathIdempotencyStore.cs:101-103
        /// requires the handler to swallow.
        /// </summary>
        public Exception? MarkCompletedThrow { get; set; }

        public ValueTask<SlackFastPathIdempotencyResult> TryAcquireAsync(
            string key,
            SlackInboundEnvelope envelope,
            TimeSpan? lifetime = null,
            CancellationToken ct = default)
        {
            this.AcquireCalls.Add(key);
            return new ValueTask<SlackFastPathIdempotencyResult>(this.Plan);
        }

        public ValueTask ReleaseAsync(string key, CancellationToken ct = default)
        {
            this.ReleaseCalls.Add(key);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkCompletedAsync(string key, CancellationToken ct = default)
        {
            this.MarkCompletedCalls.Add(key);
            this.MarkCompletedTokens.Add(ct);
            if (this.MarkCompletedThrow is not null)
            {
                throw this.MarkCompletedThrow;
            }

            return ValueTask.CompletedTask;
        }
    }
}
