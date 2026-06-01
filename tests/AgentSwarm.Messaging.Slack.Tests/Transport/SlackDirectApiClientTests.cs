// -----------------------------------------------------------------------
// <copyright file="SlackDirectApiClientTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlackNet;
using SlackNet.WebApi;
using Xunit;

/// <summary>
/// Stage 6.4 unit tests for <see cref="SlackDirectApiClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pins the three brief Test Scenarios for Stage 6.4 (lines 384-386 of
/// <c>implementation-plan.md</c>):
/// </para>
/// <list type="number">
///   <item><description><b>Modal opens within deadline</b> -- valid
///   trigger + payload routes through SlackNet's
///   <c>views.open</c> and an audit row with
///   <c>request_type = modal_open</c> + <c>outcome = success</c> is
///   appended.</description></item>
///   <item><description><b>Expired trigger returns error</b> --
///   SlackNet's <see cref="SlackException"/> (raised by Slack's
///   <c>{"ok":false,"error":"expired_trigger_id"}</c>) is mapped to a
///   <see cref="SlackDirectApiResult"/> with an ephemeral message and
///   no retry is scheduled.</description></item>
///   <item><description><b>Rate limiter shared with dispatcher</b> --
///   the client acquires from the same
///   <see cref="ISlackRateLimiter"/> instance the
///   <see cref="SlackOutboundDispatcher"/> uses (Tier 4, scope =
///   <c>team_id</c>) and an HTTP 429 from Slack feeds the
///   <c>Retry-After</c> back into that same limiter via
///   <see cref="ISlackRateLimiter.NotifyRetryAfter"/>.</description></item>
/// </list>
/// </remarks>
public sealed class SlackDirectApiClientTests
{
    private const string TeamId = "T01TEAM";
    private const string SecretRef = "test://bot-token/T01TEAM";
    private const string BotToken = "xoxb-test-bot-token";
    private const string TriggerId = "trig.X";

    [Fact]
    public async Task OpenModalAsync_invokes_views_open_via_SlackNet_with_trigger_id_and_view()
    {
        // Scenario: Modal opens within deadline.
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        Dictionary<string, object>? capturedArgs = null;
        string? capturedMethod = null;
        string? receivedToken = null;
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, object>, CancellationToken>((method, args, _) =>
            {
                capturedMethod = method;
                capturedArgs = args;
            })
            .Returns(Task.CompletedTask);

        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit, capturedTokenCallback: token => receivedToken = token);

        object view = new { type = "modal", title = new { type = "plain_text", text = "Review TASK-42" } };
        SlackModalPayload payload = new(TeamId, view)
        {
            CorrelationId = "corr-1",
            UserId = "U01USER",
            ChannelId = "C01CHAN",
            SubCommand = "review",
        };

        SlackDirectApiResult result = await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Kind.Should().Be(SlackViewsOpenResultKind.Ok);
        result.EphemeralMessage.Should().BeNull("a successful views.open does not emit an ephemeral message");

        capturedMethod.Should().Be(SlackDirectApiClient.ViewsOpenApiMethod);
        capturedArgs.Should().NotBeNull();
        capturedArgs![SlackDirectApiClient.TriggerIdArgKey].Should().Be(TriggerId);
        capturedArgs[SlackDirectApiClient.ViewArgKey].Should().BeSameAs(view,
            "the SlackNet call must receive the renderer's view payload as-is so the wire body matches what Slack expects");

        receivedToken.Should().Be(BotToken, "the workspace bot token must be passed to SlackNet's per-workspace client factory");

        limiter.Acquires.Should().ContainSingle()
            .Which.Should().Be((SlackApiTier.Tier4, TeamId),
                "views.open is Tier 4 (workspace-scoped) and MUST acquire from the same shared limiter the SlackOutboundDispatcher uses");
        limiter.RetryAfterNotifications.Should().BeEmpty("a successful call does not signal back-pressure");
    }

    [Fact]
    public async Task OpenModalAsync_writes_audit_entry_with_modal_open_request_type_and_success_outcome()
    {
        // Scenario: Modal opens within deadline -- audit entry pin.
        Mock<ISlackApiClient> apiMock = BuildSuccessfulApiClient();
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" })
        {
            CorrelationId = "corr-success-1",
            UserId = "U01USER",
            ChannelId = "C01CHAN",
            SubCommand = "review",
        };

        await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        SlackAuditEntry entry = audit.Entries.Should().ContainSingle().Subject;
        entry.RequestType.Should().Be(SlackModalAuditRecorder.RequestTypeModalOpen);
        entry.Direction.Should().Be(SlackModalAuditRecorder.DirectionInbound);
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeSuccess);
        entry.TeamId.Should().Be(TeamId);
        entry.ChannelId.Should().Be("C01CHAN");
        entry.UserId.Should().Be("U01USER");
        entry.CorrelationId.Should().Be("corr-success-1");
        entry.CommandText.Should().Be("/agent review");
        entry.ErrorDetail.Should().BeNull("success does not populate error_detail");
        entry.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task OpenModalAsync_returns_ephemeral_error_when_Slack_rejects_trigger_as_expired()
    {
        // Scenario: Expired trigger returns error.
        SlackException expired = NewSlackException("expired_trigger_id");
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        int callCount = 0;
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callCount++)
            .ThrowsAsync(expired);

        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" })
        {
            SubCommand = "review",
        };

        SlackDirectApiResult result = await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Kind.Should().Be(SlackViewsOpenResultKind.SlackError);
        result.Error.Should().Be("expired_trigger_id");
        result.EphemeralMessage.Should().NotBeNullOrEmpty(
            "every failure must produce an ephemeral message so the controller can surface it to the user (architecture.md §2.15)");
        result.EphemeralMessage.Should().Contain("expired",
            "the canned message for expired_trigger_id must mention 'expired' so the user understands why the modal did not open");

        callCount.Should().Be(1, "trigger_id is single-use; the client MUST NOT retry an expired trigger");
        limiter.RetryAfterNotifications.Should().BeEmpty(
            "Slack-side errors are not throttling signals and must not pause the shared rate-limit bucket");

        SlackAuditEntry entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.ErrorDetail.Should().Contain("expired_trigger_id");
        entry.RequestType.Should().Be(SlackModalAuditRecorder.RequestTypeModalOpen);
    }

    [Fact]
    public async Task OpenModalAsync_does_not_enqueue_for_retry_on_failure()
    {
        // Brief item 5 pin: failures do NOT route through the durable
        // outbound queue (the trigger_id is already expired by the time
        // the queue could drain). The client surface itself does not
        // expose any retry / enqueue path -- this test asserts the
        // result is purely informational and the audit row is the only
        // durable side-effect.
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(NewSlackException("invalid_blocks"));
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        SlackDirectApiResult result = await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.EphemeralMessage.Should().NotBeNullOrEmpty();

        limiter.Acquires.Should().ContainSingle("the synchronous fast-path issues exactly one views.open attempt; failures are NOT retried");
    }

    [Fact]
    public async Task OpenModalAsync_acquires_rate_limit_token_from_shared_Tier4_workspace_scope_before_calling_Slack()
    {
        // Scenario: Rate limiter shared with dispatcher (acquire side).
        Mock<ISlackApiClient> apiMock = BuildSuccessfulApiClient();
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        limiter.Acquires.Should().ContainSingle()
            .Which.Should().Be((SlackApiTier.Tier4, TeamId),
                "the shared limiter contract: views.open is Tier 4 (architecture.md §2.12); scope key is the workspace id alone (workspace-scoped tier)");
        limiter.AcquireBeforePost.Should().BeTrue(
            "the limiter MUST be acquired BEFORE SlackNet is invoked so back-pressure actually throttles the call");
    }

    [Fact]
    public async Task OpenModalAsync_feeds_HTTP_429_RetryAfter_back_into_shared_limiter()
    {
        // Scenario: Rate limiter shared with dispatcher (back-pressure side).
        TimeSpan retryAfter = TimeSpan.FromSeconds(7);
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SlackRateLimitException(retryAfter));

        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" })
        {
            SubCommand = "escalate",
        };

        SlackDirectApiResult result = await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Kind.Should().Be(SlackViewsOpenResultKind.SlackError);
        result.Error.Should().Be("rate_limited");
        result.EphemeralMessage.Should().Contain("rate-limited",
            "users hit by Slack rate limits must see a wording that distinguishes throttling from other failures so they know retrying soon will work");

        limiter.RetryAfterNotifications.Should().ContainSingle()
            .Which.Should().Be((SlackApiTier.Tier4, TeamId, retryAfter),
                "the 429 Retry-After MUST be surfaced into the SAME shared limiter the SlackOutboundDispatcher uses so chat.postMessage / chat.update calls on the same workspace also pause -- otherwise they would burn through Slack's published Tier 4 ceiling immediately after a 429");

        SlackAuditEntry entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.ErrorDetail.Should().Contain("rate_limited");
    }

    [Fact]
    public async Task OpenModalAsync_uses_default_RetryAfter_when_Slack_omits_the_header()
    {
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SlackRateLimitException(retryAfter: null));

        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        limiter.RetryAfterNotifications.Should().ContainSingle()
            .Which.Item3.Should().Be(SlackDirectApiClient.DefaultRateLimitPause,
                "a 429 without a Retry-After header MUST still hold the bucket for a sensible minimum window so the dispatcher does not immediately hammer Slack again");
    }

    [Fact]
    public async Task OpenModalAsync_classifies_unknown_exceptions_as_NetworkFailure()
    {
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dns failure"));

        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        SlackDirectApiResult result = await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        result.Kind.Should().Be(SlackViewsOpenResultKind.NetworkFailure);
        result.EphemeralMessage.Should().Contain("Slack timed out or was unreachable",
            "transport failures get a user-facing 'retry in a few seconds' message distinct from Slack-side errors");

        audit.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OpenModalAsync_short_circuits_on_missing_trigger_id_without_calling_Slack(string? badTriggerId)
    {
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        int postCount = 0;
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Callback(() => postCount++)
            .Returns(Task.CompletedTask);
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        SlackDirectApiResult result = await client.OpenModalAsync(badTriggerId!, payload, CancellationToken.None);

        result.Kind.Should().Be(SlackViewsOpenResultKind.MissingConfiguration);
        result.EphemeralMessage.Should().NotBeNullOrEmpty();
        postCount.Should().Be(0, "we must never burn a SlackNet call on a malformed trigger");
        limiter.Acquires.Should().BeEmpty("missing trigger_id is a programmer error; do not consume a rate-limit token");

        // We DO still audit the attempt so the operator can see the malformed input.
        audit.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
    }

    [Fact]
    public async Task OpenModalAsync_returns_MissingConfiguration_when_workspace_is_unregistered()
    {
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(
            apiMock,
            limiter,
            audit,
            workspaces: new Dictionary<string, SlackWorkspaceConfig>());

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        SlackDirectApiResult result = await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        result.Kind.Should().Be(SlackViewsOpenResultKind.MissingConfiguration);
        result.EphemeralMessage.Should().Contain("not configured");

        apiMock.Verify(
            x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an unregistered workspace must not reach SlackNet");
        limiter.Acquires.Should().BeEmpty("we must not acquire a rate-limit token before we know we can even authenticate");
    }

    [Fact]
    public async Task OpenAsync_satisfies_ISlackViewsOpenClient_and_does_NOT_double_audit_when_handler_audit_pipeline_already_runs()
    {
        // The handler (DefaultSlackModalFastPathHandler) already calls
        // SlackModalAuditRecorder.RecordSuccess/RecordError on its
        // outcome, so when the direct client supersedes
        // HttpClientSlackViewsOpenClient through the ISlackViewsOpenClient
        // seam, the transport must NOT write a second modal_open row --
        // otherwise the audit log would show two rows per fast-path
        // call, conflating handler-level and transport-level outcomes.
        // The high-level OpenModalAsync surface (covered in the other
        // tests above) is the one that DOES write the audit row, for
        // callers that own the lifecycle end-to-end.
        Mock<ISlackApiClient> apiMock = BuildSuccessfulApiClient();
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        ISlackViewsOpenClient client = BuildClient(apiMock, limiter, audit);

        SlackViewsOpenResult result = await client.OpenAsync(
            new SlackViewsOpenRequest(TeamId, TriggerId, new { type = "modal" }),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        audit.Entries.Should().BeEmpty(
            "the ISlackViewsOpenClient.OpenAsync compat path is used inside the handler chain, which itself writes the audit row -- a second write here would duplicate every modal_open audit entry in production");
        limiter.Acquires.Should().ContainSingle()
            .Which.Should().Be((SlackApiTier.Tier4, TeamId),
                "the rate-limit acquisition still runs on the compat path so concurrent dispatcher calls are throttled");
    }

    [Fact]
    public async Task OpenAsync_returns_failure_result_without_writing_audit_on_SlackException()
    {
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(NewSlackException("expired_trigger_id"));
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        ISlackViewsOpenClient client = BuildClient(apiMock, limiter, audit);

        SlackViewsOpenResult result = await client.OpenAsync(
            new SlackViewsOpenRequest(TeamId, TriggerId, new { type = "modal" }),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Kind.Should().Be(SlackViewsOpenResultKind.SlackError);
        result.Error.Should().Be("expired_trigger_id");
        audit.Entries.Should().BeEmpty(
            "the compat path does not own audit; the handler / SlackModalAuditRecorder pipeline does");
    }

    [Fact]
    public async Task OpenModalAsync_propagates_OperationCanceledException_when_caller_token_is_cancelled()
    {
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Dictionary<string, object>, CancellationToken>((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        Func<Task> act = async () => await client.OpenModalAsync(TriggerId, payload, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "request cancellation MUST propagate so the controller can finish the request with the appropriate aborted-response semantics");
    }

    /// <summary>
    /// Concurrent call pin: two callers issuing <c>views.open</c> in
    /// parallel BOTH acquire from the SAME shared limiter. Together
    /// with the per-bucket TOKEN math this is what keeps the combined
    /// connector + dispatcher request rate inside Slack's published
    /// tier ceilings (the third Stage 6.4 brief test scenario).
    /// </summary>
    [Fact]
    public async Task Concurrent_OpenModalAsync_calls_funnel_through_the_same_shared_rate_limiter_instance()
    {
        Mock<ISlackApiClient> apiMock = BuildSuccessfulApiClient();
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(apiMock, limiter, audit);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        Task<SlackDirectApiResult> t1 = client.OpenModalAsync("trig.A", payload, CancellationToken.None);
        Task<SlackDirectApiResult> t2 = client.OpenModalAsync("trig.B", payload, CancellationToken.None);
        await Task.WhenAll(t1, t2);

        t1.Result.IsSuccess.Should().BeTrue();
        t2.Result.IsSuccess.Should().BeTrue();
        limiter.Acquires.Should().HaveCount(2);
        limiter.Acquires.Should().AllSatisfy(call => call.Should().Be((SlackApiTier.Tier4, TeamId)),
            "every parallel views.open call MUST funnel through the same (Tier 4, workspace) bucket so the combined rate stays within Slack's per-tier ceiling");
    }

    /// <summary>
    /// Evaluator iter-2 item #3 (deadline): a slow SlackNet call that
    /// runs past the configured trigger_id deadline MUST surface as a
    /// <see cref="SlackViewsOpenResultKind.NetworkFailure"/> with an
    /// ephemeral message dedicated to the deadline path, NOT propagate
    /// as <see cref="OperationCanceledException"/>. The caller's
    /// HTTP request is still alive and the controller must respond
    /// with a user-visible ephemeral retry message rather than aborting
    /// the request.
    /// </summary>
    [Fact]
    public async Task OpenModalAsync_returns_NetworkFailure_when_deadline_fires_before_SlackNet_responds()
    {
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Dictionary<string, object>, CancellationToken>(async (_, _, ct) =>
            {
                // Block well past the deadline; the linked CTS the
                // client wraps around our token will cancel this
                // delay when the deadline trips, surfacing an OCE
                // that the SUT must translate to NetworkFailure.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            });

        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(
            apiMock,
            limiter,
            audit,
            triggerIdDeadline: TimeSpan.FromMilliseconds(75));

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" })
        {
            CorrelationId = "deadline-corr",
            SubCommand = "review",
        };

        SlackDirectApiResult result = await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Kind.Should().Be(SlackViewsOpenResultKind.NetworkFailure,
            "a deadline trip is a transport-level failure, NOT cancellation -- the caller is still alive and expects a response");
        result.Error.Should().Contain("trigger_id deadline",
            "the error tag pinpoints WHY the call failed so an operator triaging the audit log can distinguish a deadline trip from a generic network failure");
        result.EphemeralMessage.Should().NotBeNullOrEmpty();
        result.EphemeralMessage.Should().Contain("trigger_id expired",
            "the user-facing message specifically blames the trigger_id expiry so the user knows a fresh command is required (the same trigger cannot be retried)");

        // The audit row MUST land so an operator querying the audit
        // log sees the deadline trip with outcome=error.
        SlackAuditEntry entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(SlackModalAuditRecorder.OutcomeError);
        entry.RequestType.Should().Be(SlackModalAuditRecorder.RequestTypeModalOpen);
        entry.ErrorDetail.Should().Contain("trigger_id deadline");
    }

    /// <summary>
    /// Evaluator iter-2 item #3 (deadline): the deadline guard MUST
    /// NOT mask caller-driven cancellation. When the caller's token
    /// is cancelled the client still surfaces
    /// <see cref="OperationCanceledException"/> -- the
    /// HTTP-request-aborted contract is preserved regardless of how
    /// short the deadline is. This complements
    /// <see cref="OpenModalAsync_propagates_OperationCanceledException_when_caller_token_is_cancelled"/>
    /// by combining caller cancellation with a non-default deadline.
    /// </summary>
    [Fact]
    public async Task OpenModalAsync_propagates_caller_cancellation_even_with_short_deadline_configured()
    {
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Dictionary<string, object>, CancellationToken>((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        RecordingRateLimiter limiter = new();
        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClient(
            apiMock,
            limiter,
            audit,
            triggerIdDeadline: TimeSpan.FromMilliseconds(50));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        Func<Task> act = async () => await client.OpenModalAsync(TriggerId, payload, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller cancellation still wins over the deadline guard -- the controller must see the abort signal");
    }

    /// <summary>
    /// Evaluator iter-2 item #5 (real shared-limiter test scenario):
    /// pins the contract that <see cref="SlackDirectApiClient"/> and
    /// <see cref="SlackOutboundDispatcher"/> consume the SAME
    /// <see cref="SlackTokenBucketRateLimiter"/> instance. When the
    /// direct client observes an HTTP 429 it calls
    /// <see cref="ISlackRateLimiter.NotifyRetryAfter"/> on the shared
    /// instance; the dispatcher's own subsequent <c>views.update</c>
    /// (Tier 4, same workspace) acquire MUST block on that suspension
    /// -- proving the back-pressure crosses caller boundaries through
    /// the registered singleton rather than each caller maintaining
    /// its own private bucket state.
    /// </summary>
    [Fact]
    public async Task OpenModalAsync_HTTP_429_back_pressure_throttles_dispatcher_acquires_on_same_workspace_via_shared_SlackTokenBucketRateLimiter()
    {
        // Real token-bucket limiter wired with a fake clock so the
        // suspend deadline is deterministic. Both the client and the
        // simulated "dispatcher acquire" go through THIS singleton,
        // mirroring the production DI registration in
        // AddSlackOutboundDispatcher + AddSlackInboundTransport.
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        SlackConnectorOptions options = new();
        StubOptionsMonitor<SlackConnectorOptions> monitor = new(options);
        SlackTokenBucketRateLimiter sharedLimiter = new(monitor, clock);

        TimeSpan retryAfter = TimeSpan.FromSeconds(3);
        Mock<ISlackApiClient> apiMock = new(MockBehavior.Loose);
        apiMock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SlackRateLimitException(retryAfter));

        InMemorySlackAuditEntryWriter audit = new();
        SlackDirectApiClient client = BuildClientWithSharedLimiter(apiMock, sharedLimiter, audit, clock);

        SlackModalPayload payload = new(TeamId, View: new { type = "modal" });

        // 1. Direct-client views.open hits 429 -> NotifyRetryAfter
        //    suspends the (Tier 4, TeamId) bucket on the SHARED
        //    limiter.
        SlackDirectApiResult result = await client.OpenModalAsync(TriggerId, payload, CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("rate_limited");

        // 2. The dispatcher's NEXT views.update / chat.update call
        //    (Tier 4, same workspace, same scope key) MUST observe
        //    the suspension via the shared singleton -- if the
        //    client and dispatcher maintained separate limiters this
        //    acquire would return immediately and Slack would receive
        //    another request inside the Retry-After window.
        using CancellationTokenSource immediateCts = new();
        Task acquireDuringSuspension = Task.Run(async () =>
        {
            await sharedLimiter.AcquireAsync(SlackApiTier.Tier4, TeamId, immediateCts.Token);
        });

        // Give the AcquireAsync loop a real moment to enter its
        // Task.Delay branch (it MUST be blocked -- the bucket is
        // suspended for retryAfter from "now").
        await Task.Delay(50);
        acquireDuringSuspension.IsCompleted.Should().BeFalse(
            "the shared limiter MUST be holding the dispatcher's acquire until the Retry-After window elapses -- the direct client's NotifyRetryAfter has paused the (Tier 4, workspace) bucket for the entire pipeline, not just for itself");

        // 3. Cancel the blocked acquire so the test does not hang
        //    waiting for the real (wall-clock) Retry-After window.
        //    The cancellation must propagate as OperationCanceledException
        //    -- proving the call genuinely waited inside AcquireAsync
        //    rather than completing synchronously before our cancel.
        immediateCts.Cancel();
        Func<Task> awaitBlocked = async () => await acquireDuringSuspension;
        await awaitBlocked.Should().ThrowAsync<OperationCanceledException>(
            "the cancellation of the suspension-bound AcquireAsync proves the limiter was genuinely holding the bucket closed for the dispatcher's call");
    }

    /// <summary>
    /// Lightweight <see cref="IOptionsMonitor{TOptions}"/> over a single
    /// resolved options instance. Used by the shared-limiter integration
    /// test to satisfy <see cref="SlackTokenBucketRateLimiter"/>'s ctor
    /// without spinning up an entire Options pipeline.
    /// </summary>
    private sealed class StubOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    {
        public StubOptionsMonitor(TOptions value)
        {
            this.CurrentValue = value;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => this.CurrentValue;

        public IDisposable OnChange(Action<TOptions, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// Deterministic <see cref="TimeProvider"/> for the shared-limiter
    /// integration test. Mirrors the in-test fake used by other Slack
    /// suites (notably <c>SlackThreadManagerTests.FakeTimeProvider</c>)
    /// so we do not introduce a new package dependency.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        public FakeTimeProvider(DateTimeOffset initial)
        {
            this.Now = initial;
        }

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    private static SlackDirectApiClient BuildClient(
        Mock<ISlackApiClient> apiMock,
        RecordingRateLimiter limiter,
        InMemorySlackAuditEntryWriter audit,
        IReadOnlyDictionary<string, SlackWorkspaceConfig>? workspaces = null,
        Action<string>? capturedTokenCallback = null,
        TimeSpan? triggerIdDeadline = null)
    {
        Dictionary<string, SlackWorkspaceConfig> defaultWorkspaces = new()
        {
            [TeamId] = new SlackWorkspaceConfig
            {
                TeamId = TeamId,
                BotTokenSecretRef = SecretRef,
                Enabled = true,
            },
        };

        StubWorkspaceStore store = new(workspaces ?? defaultWorkspaces);
        StubSecretProvider secrets = new();
        secrets.Set(SecretRef, BotToken);

        // Wire the recording rate-limiter so the test can observe both
        // Acquire and NotifyRetryAfter calls in order. The recorder also
        // stamps "AcquireBeforePost = true" once the first acquire
        // observes that no SlackNet call has been issued yet -- this is
        // how we prove the limiter is consulted BEFORE the HTTP call.
        limiter.OnAcquire = () => limiter.AcquireBeforePost = !apiMock.Invocations.Any(i => i.Method.Name == nameof(ISlackApiClient.Post));

        return new SlackDirectApiClient(
            store,
            secrets,
            limiter,
            audit,
            NullLogger<SlackDirectApiClient>.Instance,
            apiClientFactory: token =>
            {
                capturedTokenCallback?.Invoke(token);
                return apiMock.Object;
            },
            timeProvider: TimeProvider.System,
            triggerIdDeadline: triggerIdDeadline);
    }

    private static SlackDirectApiClient BuildClientWithSharedLimiter(
        Mock<ISlackApiClient> apiMock,
        ISlackRateLimiter sharedLimiter,
        InMemorySlackAuditEntryWriter audit,
        TimeProvider? timeProvider = null)
    {
        Dictionary<string, SlackWorkspaceConfig> workspaces = new()
        {
            [TeamId] = new SlackWorkspaceConfig
            {
                TeamId = TeamId,
                BotTokenSecretRef = SecretRef,
                Enabled = true,
            },
        };

        StubWorkspaceStore store = new(workspaces);
        StubSecretProvider secrets = new();
        secrets.Set(SecretRef, BotToken);

        return new SlackDirectApiClient(
            store,
            secrets,
            sharedLimiter,
            audit,
            NullLogger<SlackDirectApiClient>.Instance,
            apiClientFactory: _ => apiMock.Object,
            timeProvider: timeProvider ?? TimeProvider.System);
    }

    private static Mock<ISlackApiClient> BuildSuccessfulApiClient()
    {
        Mock<ISlackApiClient> mock = new(MockBehavior.Loose);
        mock
            .Setup(x => x.Post(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static SlackException NewSlackException(string errorCode)
    {
        return new SlackException(new ErrorResponse { Error = errorCode });
    }

    /// <summary>
    /// In-memory <see cref="ISlackRateLimiter"/> that records every
    /// acquire and every Retry-After surface so tests can assert the
    /// SHARED-state contract with <see cref="SlackOutboundDispatcher"/>
    /// without booting a real <see cref="SlackTokenBucketRateLimiter"/>.
    /// </summary>
    private sealed class RecordingRateLimiter : ISlackRateLimiter
    {
        public List<(SlackApiTier Tier, string ScopeKey)> Acquires { get; } = new();

        public List<(SlackApiTier Tier, string ScopeKey, TimeSpan Delay)> RetryAfterNotifications { get; } = new();

        public Action? OnAcquire { get; set; }

        public bool AcquireBeforePost { get; set; }

        public ValueTask AcquireAsync(SlackApiTier tier, string scopeKey, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            this.OnAcquire?.Invoke();
            lock (this.Acquires)
            {
                this.Acquires.Add((tier, scopeKey));
            }

            return ValueTask.CompletedTask;
        }

        public void NotifyRetryAfter(SlackApiTier tier, string scopeKey, TimeSpan delay)
        {
            lock (this.RetryAfterNotifications)
            {
                this.RetryAfterNotifications.Add((tier, scopeKey, delay));
            }
        }
    }

    private sealed class StubWorkspaceStore : ISlackWorkspaceConfigStore
    {
        private readonly IReadOnlyDictionary<string, SlackWorkspaceConfig> workspaces;

        public StubWorkspaceStore(IReadOnlyDictionary<string, SlackWorkspaceConfig> workspaces)
        {
            this.workspaces = workspaces;
        }

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(teamId) && this.workspaces.TryGetValue(teamId, out SlackWorkspaceConfig? cfg))
            {
                return Task.FromResult<SlackWorkspaceConfig?>(cfg);
            }

            return Task.FromResult<SlackWorkspaceConfig?>(null);
        }

        public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(new List<SlackWorkspaceConfig>(this.workspaces.Values));
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public void Set(string secretRef, string value) => this.values[secretRef] = value;

        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            if (this.values.TryGetValue(secretRef, out string? v))
            {
                return Task.FromResult(v);
            }

            throw new SecretNotFoundException(secretRef);
        }
    }
}
