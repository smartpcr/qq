// -----------------------------------------------------------------------
// <copyright file="DecisionAuditFirstOrderingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.3 iter-8 evaluator item 3 — pins AUDIT-FIRST ordering on
/// the <c>/approve</c> and <c>/reject</c> slash-command paths. The
/// Stage 5.3 brief mandates "<i>log every outbound decision event
/// with full context</i>"; the iter-7 evaluator flagged that
/// pre-iter-8 the handlers published BEFORE auditing, which let a
/// transient audit-DB failure leak an outbound
/// <see cref="HumanDecisionEvent"/> without a durable
/// <c>audit_logs</c> row. These tests assert the strict invariant:
/// if the audit write throws, the bus publish MUST NOT run.
/// </summary>
/// <remarks>
/// Sibling tests:
/// <list type="bullet">
///   <item><description><c>CallbackQueryHandlerTests.CallbackResponse_AuditFailureReleasesBothReservations_AndRetrySucceeds</c>
///   — pins audit-first for the callback button path.</description></item>
///   <item><description><c>QuestionTimeoutServiceTests.SweepOnceAsync_AuditFailsBeforePublish_RevertsRowToPriorStatusForNextSweep_AndPublishNeverRuns</c>
///   — pins audit-first for the timeout sweep path.</description></item>
/// </list>
/// Together these three tests cover ALL outbound-decision sites
/// the iter-7 evaluator named (DecisionCommandHandlers.cs,
/// CallbackQueryHandler.cs, QuestionTimeoutService.cs) so a future
/// regression at ANY of the three sites surfaces as a test failure
/// with the iter-8 marker in the assertion message.
/// </remarks>
public sealed class DecisionAuditFirstOrderingTests
{
    [Fact]
    public async Task Approve_AuditFails_PublishNeverRuns_AndClaimReverts()
    {
        // Stage 5.3 iter-8 evaluator item 3 — pin AUDIT-FIRST for
        // the /approve command. The audit mock throws; the bus mock
        // is strict so any PublishHumanDecisionAsync call would fail
        // the test by itself. The test additionally asserts the
        // claim was reverted so the operator's retry can re-emit.
        var (handler, bus, audit, store) = BuildApprove();
        store.Setup(s => s.GetAsync("Q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewPendingQuestion("Q1", "corr-1"));
        store.Setup(s => s.MarkAnsweredAsync("Q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var revertCalls = 0;
        store.Setup(s => s.TryRevertAnsweredClaimAsync("Q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                revertCalls++;
                return true;
            });

        // Strict bus mock — if the handler calls PublishHumanDecisionAsync
        // it throws MockException. This is the load-bearing pin: with
        // the iter-8 audit-first ordering the bus publish MUST NEVER
        // run when audit fails.

        audit.Setup(a => a.LogHumanResponseAsync(It.IsAny<HumanResponseAuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated audit failure (test fixture)"));

        var act = async () => await handler.HandleAsync(
            TestCommands.Build("approve", "Q1"),
            TestOperator.Default,
            default);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "audit failed and Stage 5.3 iter-8 evaluator item 3 requires the failure to propagate so the operator sees the error rather than a silent un-audited approval");

        // Bus mock is strict — no Publish setup, so any call would
        // throw MockException. If audit-first ordering is broken
        // and the handler published BEFORE auditing, this verify
        // would fail with "expected Times.Never but was Times.Once".
        bus.Verify(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()), Times.Never,
            "Stage 5.3 iter-8 evaluator item 3 (AUDIT-FIRST): a failed audit MUST short-circuit BEFORE the bus publish — otherwise an outbound HumanDecisionEvent escapes without a durable audit_logs row");

        revertCalls.Should().Be(1,
            "Stage 5.3 iter-8 evaluator item 3: an audit failure that aborts the publish MUST also revert the atomic Answered claim so the operator's retry can re-acquire and re-issue the decision cleanly");
    }

    [Fact]
    public async Task Reject_AuditFails_PublishNeverRuns_AndClaimReverts()
    {
        // Symmetric pin for /reject — the audit-first ordering lives
        // in the shared DecisionCommandHandlerBase so both handlers
        // get the guarantee, but the symmetric test ensures a future
        // refactor that diverges the two paths cannot silently
        // regress /reject without surfacing here.
        var (handler, bus, audit, store) = BuildReject();
        store.Setup(s => s.GetAsync("Q2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewPendingQuestion("Q2", "corr-2"));
        store.Setup(s => s.MarkAnsweredAsync("Q2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var revertCalls = 0;
        store.Setup(s => s.TryRevertAnsweredClaimAsync("Q2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                revertCalls++;
                return true;
            });

        audit.Setup(a => a.LogHumanResponseAsync(It.IsAny<HumanResponseAuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated audit failure (test fixture)"));

        var act = async () => await handler.HandleAsync(
            TestCommands.Build("reject", "Q2", "because"),
            TestOperator.Default,
            default);

        await act.Should().ThrowAsync<InvalidOperationException>();

        bus.Verify(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()), Times.Never,
            "Stage 5.3 iter-8 evaluator item 3 (AUDIT-FIRST) for /reject — symmetric pin with /approve");

        revertCalls.Should().Be(1,
            "Stage 5.3 iter-8 evaluator item 3: claim revert MUST also run on /reject's audit failure");
    }

    [Fact]
    public async Task Approve_HappyPath_AuditRunsBeforePublish()
    {
        // Stage 5.3 iter-8 evaluator item 3 — pin the ORDER of the
        // two side-effects on the happy path. The Moq Strict bus +
        // Strict audit mocks capture timestamps when each call lands;
        // an inversion (publish before audit) would surface as the
        // timestamp comparison failing here.
        var (handler, bus, audit, store) = BuildApprove();
        store.Setup(s => s.GetAsync("Q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewPendingQuestion("Q1", "corr-1"));
        store.Setup(s => s.MarkAnsweredAsync("Q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sequence = new List<string>();
        audit.Setup(a => a.LogHumanResponseAsync(It.IsAny<HumanResponseAuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("audit"))
            .Returns(Task.CompletedTask);
        bus.Setup(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("publish"))
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(
            TestCommands.Build("approve", "Q1"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        sequence.Should().Equal(new[] { "audit", "publish" },
            "Stage 5.3 iter-8 evaluator item 3 (AUDIT-FIRST): the audit row MUST land BEFORE the bus publish so the Stage 5.3 brief's 'log every outbound decision event with full context' guarantee holds — a publish-before-audit ordering would let a transient audit failure leak the bus event");
    }

    [Fact]
    public async Task Reject_HappyPath_AuditRunsBeforePublish()
    {
        var (handler, bus, audit, store) = BuildReject();
        store.Setup(s => s.GetAsync("Q2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewPendingQuestion("Q2", "corr-2"));
        store.Setup(s => s.MarkAnsweredAsync("Q2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sequence = new List<string>();
        audit.Setup(a => a.LogHumanResponseAsync(It.IsAny<HumanResponseAuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("audit"))
            .Returns(Task.CompletedTask);
        bus.Setup(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("publish"))
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(
            TestCommands.Build("reject", "Q2", "reason"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        sequence.Should().Equal(new[] { "audit", "publish" },
            "Stage 5.3 iter-8 evaluator item 3 (AUDIT-FIRST) for /reject — symmetric pin with /approve");
    }

    // -----------------------------------------------------------------
    // Harness — mirrors ApproveRejectCommandHandlerTests.BuildApprove /
    // BuildReject but with strict mocks at all surfaces so a missed
    // setup surfaces as a MockException rather than a default-return.
    // -----------------------------------------------------------------

    private static (
        ApproveCommandHandler handler,
        Mock<ISwarmCommandBus> bus,
        Mock<IAuditLogger> audit,
        Mock<IPendingQuestionStore> store)
        BuildApprove()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var audit = new Mock<IAuditLogger>(MockBehavior.Strict);
        var store = new Mock<IPendingQuestionStore>(MockBehavior.Strict);
        var time = new FakeTimeProvider();
        var handler = new ApproveCommandHandler(
            store.Object,
            bus.Object,
            audit.Object,
            time,
            NullLogger<ApproveCommandHandler>.Instance);
        return (handler, bus, audit, store);
    }

    private static (
        RejectCommandHandler handler,
        Mock<ISwarmCommandBus> bus,
        Mock<IAuditLogger> audit,
        Mock<IPendingQuestionStore> store)
        BuildReject()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var audit = new Mock<IAuditLogger>(MockBehavior.Strict);
        var store = new Mock<IPendingQuestionStore>(MockBehavior.Strict);
        var time = new FakeTimeProvider();
        var handler = new RejectCommandHandler(
            store.Object,
            bus.Object,
            audit.Object,
            time,
            NullLogger<RejectCommandHandler>.Instance);
        return (handler, bus, audit, store);
    }

    private static PendingQuestion NewPendingQuestion(string questionId, string correlationId) => new()
    {
        QuestionId = questionId,
        AgentId = "agent-7",
        TaskId = "TASK-7",
        Title = "Deploy?",
        Body = "Ready to deploy build #42",
        Severity = MessageSeverity.Normal,
        AllowedActions = new[]
        {
            new HumanAction { ActionId = "ap", Label = "Approve", Value = "approve" },
            new HumanAction { ActionId = "rj", Label = "Reject", Value = "reject" },
        },
        TelegramChatId = TestOperator.Default.TelegramChatId,
        TelegramMessageId = 99,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        CorrelationId = correlationId,
        Status = PendingQuestionStatus.Pending,
        StoredAt = DateTimeOffset.UtcNow,
    };
}
